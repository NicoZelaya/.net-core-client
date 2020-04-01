﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Splitio.Domain;
using Splitio.Services.Logger;
using Splitio.Services.Shared.Classes;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Splitio.Services.Common
{
    public class AuthApiClient : IAuthApiClient
    {
        private const long ExpirationRate = 600;

        private readonly ISplitLogger _log;
        private readonly ISplitioHttpClient _splitioHttpClient;
        private readonly string _url;

        public AuthApiClient(string url,
            string apiKey,
            long connectionTimeOut,
            ISplitioHttpClient splitioHttpClient = null,
            ISplitLogger log = null)
        {
            _url = url;
            _splitioHttpClient = splitioHttpClient ?? new SplitioHttpClient(apiKey, connectionTimeOut);
            _log = log ?? WrapperAdapter.GetLogger(typeof(AuthApiClient));            
        }

        #region Public Methods
        public async Task<AuthenticationResponse> AuthenticateAsync()
        {
            try
            {
                var response = await _splitioHttpClient.GetAsync(_url);

                if (response.statusCode == HttpStatusCode.OK)
                {
                    _log.Debug($"Success connection to: {_url}");

                    return GetSuccessResponse(response.content);
                }
                else if (response.statusCode >= HttpStatusCode.BadRequest && response.statusCode < HttpStatusCode.InternalServerError)
                {
                    _log.Debug($"Problem to connect to : {_url}. Response status: {response.statusCode}");

                    return new AuthenticationResponse { PushEnabled = false, Retry = false };
                }

                return new AuthenticationResponse { PushEnabled = false, Retry = true };
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);

                return new AuthenticationResponse { PushEnabled = false, Retry = false };
            }
        }
        #endregion

        #region Private Methods
        private AuthenticationResponse GetSuccessResponse(string content)
        {
            var authResponse = JsonConvert.DeserializeObject<AuthenticationResponse>(content);

            if (authResponse.PushEnabled == true)
            {
                var tokenDecoded = DecodeJwt(authResponse.Token);
                var token = JsonConvert.DeserializeObject<Jwt>(tokenDecoded);

                authResponse.Channels = GetChannels(token);
                authResponse.Expiration = GetExpiration(token);
            }

            authResponse.Retry = false;

            return authResponse;
        }

        private string GetChannels(Jwt token)
        {
            var capability = (JObject)JsonConvert.DeserializeObject(token.Capability);
            var channelsList = capability
                .Children()
                .Select(c => c.First.Path)
                .ToList();

            return string.Join(",", channelsList);
        }

        private double GetExpiration(Jwt token)
        {
            return (token.Expiration - ExpirationRate) - GetUnixTimeSeconds();
        }

        private string DecodeJwt(string token)
        {
            var split_string = token.Split('.');
            var base64EncodedBody = split_string[1];

            int mod4 = base64EncodedBody.Length % 4;
            if (mod4 > 0)
            {
                base64EncodedBody += new string('=', 4 - mod4);
            }

            var base64EncodedBytes = Convert.FromBase64String(base64EncodedBody);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        // Returns the number of seconds that have elapsed since 1970-01-01T00:00:00Z.
        private double GetUnixTimeSeconds()
        {
            return DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        }
#endregion
    }
}
