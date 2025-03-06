using System.Text;
using System.Text.Json;
using RazorPagesDotCMS.Models;
using LazyCache;
using System.Net;
using Microsoft.Extensions.Logging;

namespace RazorPagesDotCMS.Services
{
    /// <summary>
    /// Service for interacting with the dotCMS API
    /// </summary>
    public class DotCmsService : IDotCmsService
    {
        private readonly ModelHelper _modelHelper;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DotCmsService> _logger;

        private readonly string? _apiHost;
        private readonly string? _apiAuth;
        private readonly IAppCache cache;


        /// <summary>
        /// Initializes a new instance of the <see cref="DotCmsService"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory</param>
        /// <param name="configuration">The configuration</param>
        /// <param name="logger">The logger</param>
        /// <param name="cache">LazyCache</param>
        public DotCmsService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<DotCmsService> logger, IAppCache cache)
        {
            _httpClient = httpClientFactory.CreateClient();
            _configuration = configuration;
            _logger = logger;
            this.cache = cache;
            _apiHost = _configuration["dotCMS:ApiHost"];
            _apiAuth = string.IsNullOrEmpty(_configuration["dotCMS:ApiToken"])
                ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_configuration["dotCMS:ApiUserName"] + ":" + _configuration["dotCMS:ApiPassword"]))
                : "Bearer " + _configuration["dotCMS:ApiToken"];

            _logger.LogInformation("DotCmsService constructed");
            
            // Create a logger factory for ModelHelper
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
            });
            var modelHelperLogger = loggerFactory.CreateLogger<ModelHelper>();
            _modelHelper = new ModelHelper(modelHelperLogger);
        }

        /// <summary>
        /// Gets a page from the dotCMS API by its path
        /// </summary>
        /// <param name="path">The page path</param>
        /// <param name="siteId">Optional site ID</param>
        /// <param name="mode">Optional view mode (EDIT_MODE, PREVIEW_MODE, LIVE_MODE)</param>
        /// <param name="languageId">Optional language ID</param>
        /// <param name="persona">Optional persona ID</param>
        /// <param name="fireRules">Whether to fire rules (default: false)</param>
        /// <param name="depth">Depth of the content to retrieve (default: 1)</param>
        /// <returns>The page response</returns>
        public async Task<PageResponse> GetPageAsync(
            string path,
            string? siteId = null,
            PageMode? mode = PageMode.LIVE_MODE,
            string? languageId = null,
            string? persona = null,
            bool fireRules = false,
            int depth = 1)
        {
            try
            {

                path = NormalizePath(path);
                // Create the request to the dotCMS API
                var requestUrl = $"{_apiHost}/api/v1/page/json{path}";

                // Add query parameters if provided
                var uriBuilder = new UriBuilder(requestUrl);
                var query = new System.Collections.Specialized.NameValueCollection();

                if (!string.IsNullOrEmpty(siteId))
                {
                    query["siteId"] = siteId;
                }

                if (mode.HasValue)
                {
                    query["mode"] = mode.Value.ToString();
                }

                if (!string.IsNullOrEmpty(languageId))
                {
                    query["language_id"] = languageId;
                }

                if (!string.IsNullOrEmpty(persona))
                {
                    query["persona"] = persona;
                }

                // Always include fireRules and depth parameters
                query["fireRules"] = fireRules.ToString().ToLower();
                query["depth"] = depth.ToString();

                // Convert the query collection to a query string
                var queryString = string.Join("&", Array.ConvertAll(
                    query.AllKeys,
                    key => key != null
                        ? $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(query[key] ?? string.Empty)}"
                        : string.Empty
                ));

                if (!string.IsNullOrEmpty(queryString))
                {
                    uriBuilder.Query = queryString;
                }

                string finalRequestUrl = uriBuilder.Uri.ToString();
                int cacheSeconds = PageMode.LIVE_MODE == mode ? 60 : 0;

                // Use GetOrAddAsync with an async delegate
                return await cache.GetOrAdd(finalRequestUrl, async () =>
                {
                    try
                    {
                        _logger.LogInformation($"Requesting page from: {finalRequestUrl}");

                        var request = new HttpRequestMessage(HttpMethod.Get, finalRequestUrl);
                        request.Headers.Add("Authorization", _apiAuth);

                        // Send the request to dotCMS
                        var response = await _httpClient.SendAsync(request);

                        // Check if the response is successful
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning($"dotCMS API returned status code: {response.StatusCode}");
                            throw new HttpRequestException($"dotCMS API returned status code: {response.StatusCode}");
                        }

                        // Read the response content
                        var content = await response.Content.ReadAsStringAsync();

                        // Deserialize the response to PageResponse model
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };

                        var pageResponse = JsonSerializer.Deserialize<PageResponse>(content, options);
                        return pageResponse ?? throw new JsonException("Failed to deserialize page response");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error getting page from dotCMS API");
                        throw;
                    }
                }, DateTimeOffset.Now.AddSeconds(cacheSeconds));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetPageAsync");
                throw;
            }
        }

        /// <summary>
        /// Gets a page from the dotCMS API using GraphQL
        /// </summary>
        /// <param name="graphqlQuery">The GraphQL query</param>
        /// <returns>The page response</returns>
        public async Task<PageResponse> GetPageGraphqlAsync(
            string path,
            string? siteId = null,
            PageMode? mode = PageMode.LIVE_MODE,
            string? languageId = null,
            string? persona = null,
            bool fireRules = false)
        {
            try
            {

                PageMode newMode = (PageMode)(mode is null ? PageMode.LIVE_MODE : mode);  // Default to LIVE_MODE if not provided
                string graphqlQuery = GetGraphqlPageQuery(path, siteId, newMode, languageId, persona, fireRules);

                int ttlSeconds = newMode == PageMode.LIVE_MODE ? 60 : 0;
                string content = await QueryGraphqlAsync(graphqlQuery, ttlSeconds);

                // Convert the GraphQL response to PageResponse using the instance
                return _modelHelper.ConvertGraphqlToPageResponse(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetPageGraphqlAsync");
                throw;
            }
        }

        
        public async Task<string> QueryGraphqlAsync(string graphqlQuery)
        {
            return await QueryGraphqlAsync(graphqlQuery, 0);
        }
        

        public async Task<string> QueryGraphqlAsync(string graphqlQuery, int cacheSeconds)
        {

            try
            {
                // Calculate SHA256 hash of the query for the qid parameter
                string qid = CalculateSHA256(graphqlQuery);

                return await cache.GetOrAdd(qid, async () =>
                {

                    // Create the request to the dotCMS GraphQL API
                    var requestUrl = $"{_apiHost}/api/v1/graphql";

                    // Add qid as a query parameter
                    var uriBuilder = new UriBuilder(requestUrl);
                    var query = new System.Collections.Specialized.NameValueCollection
                    {
                        ["qid"] = qid
                    };

                    // Convert the query collection to a query string
                    var queryString = string.Join("&", Array.ConvertAll(
                        query.AllKeys,
                        key => key != null
                            ? $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(query[key] ?? string.Empty)}"
                            : string.Empty
                    ));

                    if (!string.IsNullOrEmpty(queryString))
                    {
                        uriBuilder.Query = queryString;
                    }

                    string finalRequestUrl = uriBuilder.Uri.ToString();

                    _logger.LogInformation($"Requesting page from GraphQL: {finalRequestUrl}");


                    // Create the request with the GraphQL query in the body
                    var request = new HttpRequestMessage(HttpMethod.Post, finalRequestUrl);
                    request.Headers.Add("Authorization", _apiAuth);

                    string jsonStr = JsonSerializer.Serialize(new
                    {
                        query = graphqlQuery
                    });




                    request.Content = new StringContent(jsonStr);
                    // Send the request to dotCMS
                    var response = await _httpClient.SendAsync(request);

                    // Check if the response is successful
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"dotCMS GraphQL API returned status code: {response.StatusCode}");
                        throw new HttpRequestException($"dotCMS GraphQL API returned status code: {response.StatusCode}");
                    }

                    // Read the response content
                    return await response.Content.ReadAsStringAsync();

                }, DateTimeOffset.Now.AddSeconds(cacheSeconds));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetGraphqlAsync");
                throw;
            }


        }



        /// <summary>
        /// Calculates the SHA256 hash of the input string
        /// </summary>
        /// <param name="input">The input string</param>
        /// <returns>The SHA256 hash as a hexadecimal string</returns>
        private static string CalculateSHA256(string input)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = sha256.ComputeHash(inputBytes);

                // Convert the byte array to a hexadecimal string
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    builder.Append(hashBytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }


        private string GetGraphqlPageQuery(string path,
                string? siteId = null,
                PageMode mode = PageMode.LIVE_MODE,
                string? languageId = null,
                string? personaId = null,
                bool fireRules = false)
        {
            path = NormalizePath(path);
            string query = "page(url: \"" + path + "\"";
            query += ",pageMode:\"" + mode.ToString() + "\"";
            query += string.IsNullOrEmpty(personaId) ? "" : ",personaId : \"" + personaId + "\"";
            query += ",fireRules :" + fireRules.ToString().ToLower();
            query += string.IsNullOrEmpty(siteId) ? "" : ",site : \"" + siteId + "\"";
            query += string.IsNullOrEmpty(languageId) ? "" : ",languageId : \"" + languageId + "\"";
            query += ")";


            string finalGraphql = graphql.Replace("${query}", query);


            _logger.LogDebug($"finalGraphql: {finalGraphql}");


            return finalGraphql;


        }

        private static string NormalizePath(string path)
        {
            // Normalize the path
            path = path ?? "/";
            path = path.EndsWith("/") ? $"{path}index" : path;
            return path;
        }

     

        private static string graphql = @"
   {
        ${query} 
        {
            _map
            urlContentMap {
                identifier
                modDate
                publishDate
                creationDate
                title
                baseType
                inode
                archived
                _map
                urlMap
                working
                locked
                contentType
                live
            }
            title
            friendlyName
            description
            tags
            canEdit
            canLock
            canRead
            template {
                inode
                identifier
                drawed
            }
            containers {
                path
                identifier
                maxContentlets
                container {
                    identifier
                    path
                    maxContentlets
                }
                containerStructures {
                    contentTypeVar
                    inode
                    identifier
                }
                containerContentlets {
                    uuid
                    contentlets {
                        identifier
                        modDate
                        publishDate
                        creationDate
                        title
                        baseType
                        inode
                        archived
                        _map
                        urlMap
                        working
                        locked
                        contentType
                        live
                    }
                }
            }
            layout {
                header
                footer
                sidebar {
                    widthPercent
                    width
                    location
                }
                body {
                    rows {
                        columns {
                            leftOffset
                            styleClass
                            width
                            left
                            containers {
                                identifier
                                uuid
                            }
                        }
                    }
                }
            }
            viewAs {
                visitor {
                    persona {
                        name
                        keyTag
                        identifier
                    }
                    device
                    tags {
                        tag
                        count
                    }
                    geo {
                        continent
                        country
                        subdivision
                        city
                        timezone
                        latitude
                        longitude
                        continentCode
                    }
                }
                language {
                    id
                    languageCode
                    countryCode
                    language
                    country
                }
            }
        }
    }
    ";


    }
}
