// Copyright © 2025 Marc Banyard
//
// This file is part of XeroMailerWeb.
//
// XeroMailerWeb is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// XeroMailerWeb is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with XeroMailerWeb. If not, see <https://www.gnu.org/licenses/>.
//
// This project remains under full copyright by Marc Banyard.
// Redistribution must retain this notice and remain under GPL v3 or
// compatible licensing.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Text.Json;

namespace XeroMailerWeb.Controllers
{
    [ApiController]
    [Route("xero")]
    public class XeroAuthController : ControllerBase
    {
        private readonly IConfiguration _config;
        public XeroAuthController(IConfiguration config)
        {
            _config = config;
        }

        private string GetDynamicRedirectUri()
        {
            var scheme = Request.Scheme;
            var host = Request.Host.Value;
            var pathBase = Request.PathBase.Value ?? "";
            var redirectUri = $"{scheme}://{host}{pathBase}/xero/callback";
            return redirectUri;
        }

        [HttpGet("debug-redirect-uri")]
        public IActionResult DebugRedirectUri()
        {
            var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
            if (!enableDetailedLogging)
            {
                return NotFound();
            }
            var redirectUri = GetDynamicRedirectUri();
            return Content($"Generated Redirect URI: {redirectUri}\n\n" +
                         $"Request.Scheme: {Request.Scheme}\n" +
                         $"Request.Host: {Request.Host.Value}\n" +
                         $"Request.PathBase: {Request.PathBase.Value ?? "(empty)"}\n" +
                         $"Request.Path: {Request.Path.Value}");
        }

        [HttpGet("connect")]
        public IActionResult ConnectToXero()
        {
            var clientId = _config["Xero:XeroClientId"];
            var redirectUri = GetDynamicRedirectUri();
            var scopes = "openid profile email accounting.transactions offline_access accounting.settings";
            var state = Guid.NewGuid().ToString();

            var url = $"https://login.xero.com/identity/connect/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scopes)}&state={state}";
            return Redirect(url);
        }

        [HttpGet("callback")]
        public async Task<IActionResult> XeroCallback([FromQuery] string code, [FromQuery] string state, [FromQuery] string? error = null)
        {
            if (!string.IsNullOrEmpty(error))
            {
                return Content($"Xero returned an error: {error}");
            }
            if (string.IsNullOrEmpty(code))
            {
                return Content("No code returned from Xero.");
            }

            var clientId = _config["Xero:XeroClientId"];
            var clientSecret = _config["Xero:XeroClientSecret"];
            var redirectUri = GetDynamicRedirectUri();

            var client = new HttpClient();
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://identity.xero.com/connect/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("code", code ?? ""),
                    new KeyValuePair<string, string>("redirect_uri", redirectUri ?? ""),
                    new KeyValuePair<string, string>("client_id", clientId ?? ""),
                    new KeyValuePair<string, string>("client_secret", clientSecret ?? "")
                })
            };
            var response = await client.SendAsync(tokenRequest);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Content($"Failed to get tokens from Xero: {content}");
            }
            var json = JsonDocument.Parse(content);
            var accessToken = json.RootElement.GetProperty("access_token").GetString();
            var refreshToken = json.RootElement.GetProperty("refresh_token").GetString();
            var expiresIn = json.RootElement.GetProperty("expires_in").GetInt32();
            var tokenType = json.RootElement.GetProperty("token_type").GetString();
            // Store tokens in a file for demonstration
            var tokenObj = new {
                access_token = accessToken,
                refresh_token = refreshToken,
                expires_in = expiresIn,
                token_type = tokenType,
                obtained_at = DateTime.UtcNow
            };
            var tokenJson = System.Text.Json.JsonSerializer.Serialize(tokenObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText("app_XeroTokens.json", tokenJson);
            return Content($"Successfully connected to Xero! \nTokens saved to app_XeroTokens.json.");
        }
    }
} 