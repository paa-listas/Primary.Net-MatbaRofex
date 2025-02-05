﻿using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Primary.Data;
using Primary.Data.Orders;
using Primary.Serialization;
using Primary.WebSockets;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Primary
{
    public class Api
    {
        /// <summary>This is the default production endpoint.</summary>
        public static Uri ProductionEndpoint => new Uri("https://api.primary.com.ar");

        /// <summary>This is the default demo endpoint.</summary>
        /// <remarks>You can get a demo username at https://remarkets.primary.ventures.</remarks>
        public static Uri DemoEndpoint => new Uri("https://api.remarkets.primary.com.ar");

        /// <summary>
        /// Build a new API object.
        /// </summary>
        public Api(Uri baseUri, HttpClient httpClient = null)
        {
            BaseUri = baseUri;
            HttpClient = httpClient ?? new HttpClient() { DefaultRequestVersion = new(2, 0) };
        }

        public Uri BaseUri { get; private set; }
        public HttpClient HttpClient { get; private set; }

        #region Login

        public string AccessToken { get; private set; }

        /// <summary>
        /// Initialize the specified environment.
        /// </summary>
        /// <param name="username">User used for authentication.</param>
        /// <param name="password">Password used for authentication.</param>
        /// <returns></returns>
        public async Task<bool> Login(string username, string password)
        {
            var uri = new Uri(BaseUri, "/auth/getToken");

            HttpClient.DefaultRequestHeaders.Clear();
            HttpClient.DefaultRequestHeaders.Add("X-Username", username);
            HttpClient.DefaultRequestHeaders.Add("X-Password", password);

            var result = await HttpClient.PostAsync(uri, new StringContent(string.Empty));

            if (result.IsSuccessStatusCode)
            {
                AccessToken = result.Headers.GetValues("X-Auth-Token").FirstOrDefault();
                HttpClient.DefaultRequestHeaders.Clear();
                HttpClient.DefaultRequestHeaders.Add("X-Auth-Token", AccessToken);
            }

            return result.IsSuccessStatusCode;
        }

        /// <summary>
        /// Logout from server
        /// </summary>
        /// <returns></returns>
        public async Task<bool> Logout()
        {
            var uri = new Uri(BaseUri, "/auth/removeToken");

            // Header already there
            // HttpClient.DefaultRequestHeaders.Add("X-Auth-Token", AccessToken.ToString());

            var result = await HttpClient.GetAsync(uri);

            if (result.IsSuccessStatusCode)
            {
                AccessToken = string.Empty;
            }

            return result.IsSuccessStatusCode;
        }

        public const string DemoUsername = "naicigam2046";
        public const string DemoPassword = "nczhmL9@";
        public const string DemoAccount = "REM2046";

        #endregion

        #region Instruments information

        /// <summary>
        /// Get all instruments currently traded on the exchange.
        /// </summary>
        /// <returns>Instruments information.</returns>
        public async Task<IEnumerable<Instrument>> GetAllInstruments()
        {
            var uri = new Uri(BaseUri, "/rest/instruments/details");
            var response = await HttpClient.GetStringAsync(uri);

            var data = JsonConvert.DeserializeObject<GetAllInstrumentsResponse>(response);
            return data.Instruments;
        }

        /// <summary>
        /// Same as <see cref="GetAllInstruments"/> but with cache capabilities
        /// </summary>
        /// <param name="fileCacheName">(optional) File to use as cache</param>
        /// <param name="cacheValidAntiquity">(optional) How long the cache is valid, in days.</param>
        /// <returns>Instruments information or null if information can not be retrieved</returns>
        public async Task<IEnumerable<Instrument>> GetAllInstrumentsFileCache(string fileCacheName = null, uint cacheValidAntiquity = 1)
        {
            IEnumerable<Instrument> instrumentDetail = null;
            Serialization.CacheInstrumentsDetails cache = null;
            bool shouldRefresh = false;

            if (fileCacheName == null)
                fileCacheName = System.IO.Path.GetTempPath() + "AllMarketInstrumentsDetailsCache.json";


            if (System.IO.File.Exists(fileCacheName) == true)
            {
                cache = InstrumentsDetailsSerializer.DesSerializeInstrumentDetail(fileCacheName);
                if (cache != null && (DateTime.Now.Date - cache.CacheDate.Date).Days < (int)cacheValidAntiquity)
                {
                    shouldRefresh = false;
                    instrumentDetail = cache.InstrumentDetailList;
                    cache = null;
                }
                else
                    shouldRefresh = true;
            }
            else
                shouldRefresh = true;

            if (shouldRefresh)
            {
                instrumentDetail = await GetAllInstruments();
                if (instrumentDetail != null)
                {
                    cache = new CacheInstrumentsDetails();
                    cache.CacheDate = DateTime.Now.Date;
                    cache.InstrumentDetailList = new List<Instrument>(instrumentDetail);
                    InstrumentsDetailsSerializer.SerializeInstrumentsDetails(cache, fileCacheName);
                    cache = null;
                }
            }

            return instrumentDetail;
        }

        private class GetAllInstrumentsResponse
        {
            [JsonProperty("instruments")]
            public List<Instrument> Instruments { get; set; }
        }

        #endregion

        #region Historical data

        /// <summary>
        /// Get historical trades for a specific instrument.
        /// </summary>
        /// <param name="instrumentId">Instrument to get information for.</param>
        /// <param name="dateFrom">First date of trading information.</param>
        /// <param name="dateTo">Last date of trading information.</param>
        /// <returns>Trade information for the instrument in the specified period.</returns>
        public async Task<IEnumerable<Trade>> GetHistoricalTrades(InstrumentId instrumentId,
                                                                    DateTime dateFrom,
                                                                    DateTime dateTo)
        {
            UriBuilder builder = new UriBuilder(BaseUri + "/rest/data/getTrades");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["marketId"] = instrumentId.Market;
            query["symbol"] = instrumentId.Symbol;
            query["dateFrom"] = dateFrom.ToString("yyyy-MM-dd");
            query["dateTo"] = dateTo.ToString("yyyy-MM-dd");
            builder.Query = query.ToString();

            var response = await HttpClient.GetStringAsync(builder.Uri);
            var data = JsonConvert.DeserializeObject<GetTradesResponse>(response);

            if (data.Status == Status.Error)
            {
                throw new Exception($"{data.Message} ({data.Description})");
            }

            return data.Trades;
        }

        private class GetTradesResponse
        {
            [JsonProperty("status")]
            public string Status;

            [JsonProperty("message")]
            public string Message;

            [JsonProperty("description")]
            public string Description;

            [JsonProperty("trades")]
            public List<Trade> Trades { get; set; }
        }

        #endregion

        #region Market data sockets

        /// <summary>
        /// Create a Market Data web socket to receive real-time market data.
        /// </summary>
        /// <param name="instruments">Instruments to watch.</param>
        /// <param name="entries">Market data entries to watch.</param>
        /// <param name="level"></param>
        /// <param name="depth">Depth of the book.</param>
        /// <returns>The market data web socket.</returns>
        public MarketDataWebSocket CreateMarketDataSocket(IEnumerable<InstrumentId> instruments,
                                                          IEnumerable<Entry> entries,
                                                          uint level, uint depth
        )
        {
            return CreateMarketDataSocket(instruments, entries, level, depth, new CancellationToken());
        }

        /// <summary>
        /// Create a Market Data web socket to receive real-time market data.
        /// </summary>
        /// <param name="instrumentIds">Instruments to watch.</param>
        /// <param name="entries">Market data entries to watch.</param>
        /// <param name="level">Real-time message update time.
        ///     <list type="table">
        ///         <listheader> <term>Level</term> <description>Update time (ms)</description> </listheader>
        ///         <item> <term>1</term> <description>100</description> </item>
        ///         <item> <term>2</term> <description>500</description> </item>
        ///         <item> <term>3</term> <description>1000</description> </item>
        ///         <item> <term>4</term> <description>3000</description> </item>
        ///         <item> <term>5</term> <description>6000</description> </item>
        ///     </list>
        /// </param>
        /// <param name="depth">Depth of the book.</param>
        /// <param name="cancellationToken">Custom cancellation token to end the socket task.</param>
        /// <returns>The market data web socket.</returns>
        public MarketDataWebSocket CreateMarketDataSocket(IEnumerable<InstrumentId> instrumentIds,
                                                          IEnumerable<Entry> entries,
                                                          uint level, uint depth,
                                                          CancellationToken cancellationToken
        )
        {
            var marketDataToRequest = new MarketDataInfo()
            {
                Depth = depth,
                Entries = entries.ToArray(),
                Level = level,
                Products = instrumentIds.ToArray()
            };

            JsonSerializerSettings instrumentsSerializationSettings = new()
            {
                Culture = CultureInfo.InvariantCulture,
                ContractResolver = new StrictTypeContractResolver(typeof(InstrumentId))
            };

            return new MarketDataWebSocket(this, marketDataToRequest, cancellationToken, instrumentsSerializationSettings);
        }

        #endregion

        #region Order data sockets

        /// <summary>
        /// Create a Order Data web socket to receive real-time orders data.
        /// </summary>
        /// <param name="accounts">Accounts to get order events from.</param>
        /// <returns>The order data web socket.</returns>
        public OrderDataWebSocket CreateOrderDataSocket(IEnumerable<string> accounts)
        {
            return CreateOrderDataSocket(accounts, new CancellationToken());
        }

        /// <summary>
        /// Create a Market Data web socket to receive real-time market data.
        /// </summary>
        /// <param name="accounts">Accounts to get order events from.</param>
        /// <param name="cancellationToken">Custom cancellation token to end the socket task.</param>
        /// <returns>The order data web socket.</returns>
        public OrderDataWebSocket CreateOrderDataSocket(IEnumerable<string> accounts,
                                                        CancellationToken cancellationToken
        )
        {
            var request = new OrderDataRequest
            {
                Accounts = accounts.Select(a => new OrderStatus.AccountId() { Id = a }).ToArray()
            };

            return new OrderDataWebSocket(this, request, cancellationToken);
        }

        #endregion

        #region Orders

        /// <summary>
        /// Send an order to the specific account.
        /// </summary>
        /// <param name="account">Account to send the order to.</param>
        /// <param name="order">Order to send.</param>
        /// <returns>Order identifier.</returns>
        public async Task<OrderId> SubmitOrder(string account, Order order)
        {
            var builder = new UriBuilder(BaseUri + "/rest/order/newSingleOrder");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["marketId"] = "ROFX";
            query["symbol"] = order.InstrumentId.Symbol;
            query["price"] = order.Price?.ToString(CultureInfo.InvariantCulture);
            query["orderQty"] = order.Quantity.ToString();
            query["ordType"] = order.Type.ToApiString();
            query["side"] = order.Side.ToApiString();
            query["timeInForce"] = order.Expiration.ToApiString();
            query["account"] = account;
            query["cancelPrevious"] = order.CancelPrevious.ToString(CultureInfo.InvariantCulture);
            query["iceberg"] = order.Iceberg.ToString(CultureInfo.InvariantCulture);

            if (order.Expiration == Expiration.GoodTillDate)
            {
                query["expireDate"] = order.ExpirationDate.ToString("yyyyMMdd");
            }

            if (order.Iceberg)
            {
                query["displayQty"] = order.DisplayQuantity.ToString(CultureInfo.InvariantCulture);
            }
            builder.Query = query.ToString();

            var jsonResponse = await HttpClient.GetStringAsync(builder.Uri);

            var response = JsonConvert.DeserializeObject<OrderIdResponse>(jsonResponse);
            if (response.Status == Status.Error)
            {
                throw new Exception($"{response.Message} ({response.Description})");
            }

            return new OrderId()
            {
                ClientOrderId = response.Order.ClientId,
                Proprietary = response.Order.Proprietary
            };
        }

        /// <summary>
        /// Get order information from identifier.
        /// </summary>
        /// <param name="orderId">Order identifier.</param>
        /// <returns>Order information.</returns>
        public async Task<OrderStatus> GetOrderStatus(OrderId orderId)
        {

            var builder = new UriBuilder(BaseUri + "/rest/order/id");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["clOrdId"] = orderId.ClientOrderId;
            query["proprietary"] = orderId.Proprietary;
            builder.Query = query.ToString();

            var jsonResponse = await HttpClient.GetStringAsync(builder.Uri);

            var response = JsonConvert.DeserializeObject<GetOrderResponse>(jsonResponse);
            if (response.Status == Status.Error)
            {
                throw new Exception($"{response.Message} ({response.Description})");
            }

            return response.Order;
        }

        /// <summary>
        /// Updates the order quantity and price.
        /// </summary>
        /// <param name="orderId">The id of the order to update.</param>
        /// <param name="newQuantity">The new order quantity.</param>
        /// <param name="newPrice">The new order price.</param>
        /// <returns>Order identifier.</returns>
        public async Task<OrderId> UpdateOrder(OrderId orderId, decimal newQuantity, decimal? newPrice)
        {
            var builder = new UriBuilder(BaseUri + "/rest/order/replaceById");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["clOrdId"] = orderId.ClientOrderId;
            query["proprietary"] = orderId.Proprietary;
            query["orderQty"] = newQuantity.ToString();

            if (newPrice != null)
            {
                query["price"] = newPrice?.ToString(CultureInfo.InvariantCulture);
            }

            builder.Query = query.ToString();

            var jsonResponse = await HttpClient.GetStringAsync(builder.Uri);

            var response = JsonConvert.DeserializeObject<OrderIdResponse>(jsonResponse);
            if (response.Status == Status.Error)
            {
                throw new Exception($"{response.Message} ({response.Description})");
            }

            return new OrderId()
            {
                ClientOrderId = response.Order.ClientId,
                Proprietary = response.Order.Proprietary
            };
        }

        private struct StatusResponse
        {
            [JsonProperty("status")]
            public string Status;

            [JsonProperty("message")]
            public string Message;

            [JsonProperty("description")]
            public string Description;
        }

        /// <summary>
        /// Cancel an order.
        /// </summary>
        /// <param name="orderId">Order identifier to cancel.</param>
        public async Task CancelOrder(OrderId orderId)
        {

            var builder = new UriBuilder(BaseUri + "/rest/order/cancelById");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["clOrdId"] = orderId.ClientOrderId;
            query["proprietary"] = orderId.Proprietary;
            builder.Query = query.ToString();

            var jsonResponse = await HttpClient.GetStringAsync(builder.Uri);

            var response = JsonConvert.DeserializeObject<StatusResponse>(jsonResponse);
            if (response.Status == Status.Error)
            {
                throw new Exception($"{response.Message} ({response.Description})");
            }
        }

        private struct OrderIdResponse
        {
            [JsonProperty("status")]
            public string Status;

            [JsonProperty("message")]
            public string Message;

            [JsonProperty("description")]
            public string Description;

            public struct Id
            {
                [JsonProperty("clientId")]
                public string ClientId { get; set; }

                [JsonProperty("proprietary")]
                public string Proprietary { get; set; }
            }

            [JsonProperty("order")]
            public Id Order;
        }

        private struct GetOrderResponse
        {
            [JsonProperty("status")]
            public string Status;

            [JsonProperty("message")]
            public string Message;

            [JsonProperty("description")]
            public string Description;

            [JsonProperty("order")]
            public OrderStatus Order { get; set; }
        }

        #endregion

        #region Accounts

        public async Task<AccountStatement> GetAccountStatement(string accountId)
        {
            var uri = new Uri(BaseUri, "/rest/risk/accountReport/" + accountId);
            var jsonResponse = await HttpClient.GetStringAsync(uri);

            var response = JsonConvert.DeserializeObject<GetAccountStatementResponse>(jsonResponse);

            //if (response.Status == Status.Error)
            //{
            //throw new Exception($"{response.Message} ({response.Description})");
            //}

            return response.AccountStatement;
        }

        private struct GetAccountStatementResponse
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("accountData")]
            public AccountStatement AccountStatement { get; set; }
        }

        #endregion

        #region Constants

        private static class Status
        {
            public const string Error = "ERROR";
        }

        #endregion
    }
}
