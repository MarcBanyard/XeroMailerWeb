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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using XeroMailerWeb.Services;
using XeroMailerWeb.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.IO;

namespace XeroMailerWeb.Services
{
    public class WebhookProcessorService : BackgroundService
    {
        private readonly WebhookQueueService _queueService;
        private readonly XeroService _xeroService;
        private readonly ILogger<WebhookProcessorService> _logger;
        private readonly IConfiguration _config;
        private static readonly string WebhookLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroWebhook.log");
        private readonly TimeSpan _delay = TimeSpan.FromSeconds(2); // Adjust as needed
        private readonly IServiceProvider _serviceProvider;

        public WebhookProcessorService(WebhookQueueService queueService, XeroService xeroService, ILogger<WebhookProcessorService> logger, IConfiguration config)
        {
            _queueService = queueService;
            _xeroService = xeroService;
            _logger = logger;
            _config = config;
        }

        private void LogToFile(string message)
        {
            var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
            if (!enableDetailedLogging)
                return;
            try
            {
                System.IO.File.AppendAllText(WebhookLogPath, $"[{DateTime.UtcNow:O}] {message}\n");
            }
            catch { /* Ignore logging errors */ }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            const double minIntervalSeconds = 1.1; // 1.1s between requests = ~54/minute
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var queue = await _queueService.GetAllAsync();
                    if (queue.Count > 0)
                    {
                        var webhookEvent = queue[0];
                        try
                        {
                            await ProcessWebhookEvent(webhookEvent);
                            await _queueService.RemoveAsync(webhookEvent); // Only remove if no exception
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("TooManyRequests"))
                            {
                                _logger.LogWarning("Rate limit hit. Will retry event on next cycle. Event remains in queue.");
                                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                            }
                            else
                            {
                                _logger.LogError(ex, "Error processing webhook event. Event remains in queue for retry.");
                                // Do not remove from queue; will retry on next cycle
                            }
                        }
                        // Wait 1.1 seconds after each attempt to enforce rate limit
                        await Task.Delay(TimeSpan.FromSeconds(minIntervalSeconds), stoppingToken);
                    }
                    else
                    {
                        await Task.Delay(_delay, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException)
                    {
                        // Service is stopping, do not log as error
                        break;
                    }
                    _logger.LogError(ex, "Error in WebhookProcessorService loop");
                    await Task.Delay(_delay, stoppingToken);
                }
            }
        }

        private async Task ProcessWebhookEvent(XeroEvent webhookEvent)
        {
            try
            {
                LogToFile($"[ProcessWebhookEvent] Processing event: {webhookEvent.EventType} for resource: {webhookEvent.ResourceId}");

                if (webhookEvent.EventCategory != "INVOICE")
                {
                    LogToFile($"[ProcessWebhookEvent] Skipping non-invoice event: {webhookEvent.EventCategory}");
                    return;
                }
                if (webhookEvent.EventType != "CREATE" && webhookEvent.EventType != "UPDATE")
                {
                    LogToFile($"[ProcessWebhookEvent] Skipping event type: {webhookEvent.EventType}");
                    return;
                }
                var invoiceId = ExtractInvoiceIdFromResourceUri(webhookEvent.ResourceUri);
                if (string.IsNullOrEmpty(invoiceId))
                {
                    _logger.LogWarning($"[ProcessWebhookEvent] Could not extract invoice ID from resource URI: {webhookEvent.ResourceUri}");
                    LogToFile($"[ProcessWebhookEvent] Could not extract invoice ID from resource URI: {webhookEvent.ResourceUri}");
                    return;
                }
                var invoice = await _xeroService.GetInvoiceDetailsAsync(webhookEvent.TenantId, invoiceId);
                if (invoice == null)
                {
                    _logger.LogWarning($"[ProcessWebhookEvent] Could not fetch invoice details for {invoiceId}");
                    LogToFile($"[ProcessWebhookEvent] Could not fetch invoice details for {invoiceId}");
                    return;
                }
                var invoiceElement = invoice.Value;
                //LogToFile($"[ProcessWebhookEvent] Invoice JSON for {invoiceId}: {invoiceElement}");
                var status = invoiceElement.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() : null;
                var sentToContact = invoiceElement.TryGetProperty("SentToContact", out var sentProp) && sentProp.GetBoolean();
                LogToFile($"[ProcessWebhookEvent] Invoice {invoiceId} status: {status}, SentToContact: {sentToContact}");
                if (!string.Equals(status, "AUTHORISED", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase))
                {
                    LogToFile($"[ProcessWebhookEvent] Invoice {invoiceId} is not AUTHORISED or PAID (status: {status}), skipping email.");
                    return;
                }
                if (!sentToContact)
                {
                    XeroService.SaveSentToContactState(invoiceId, false);
                    var emailsToSend = XeroService.ExtractContactEmails(invoiceElement);
                    if (emailsToSend.Count > 0)
                    {
                        LogToFile($"[ProcessWebhookEvent] Invoice {invoiceId} is AUTHORISED but not SentToContact, setting SentToContact=true via API.");
                        await _xeroService.SetInvoiceSentToContactAsync(webhookEvent.TenantId, invoiceId);
                    }
                    else
                    {
                        _logger.LogWarning($"[ProcessWebhookEvent] No customer emails found for invoice {invoiceId}, not marking as sent.");
                        LogToFile($"[ProcessWebhookEvent] No customer emails found for invoice {invoiceId}, not marking as sent.");
                    }
                    return;
                }
                var emails = XeroService.ExtractContactEmails(invoiceElement);
                LogToFile($"[ProcessWebhookEvent] Extracted emails for invoice {invoiceId}: {string.Join(", ", emails)}");
                if (emails.Count > 0)
                {
                    foreach (var email in emails)
                    {
                        await _xeroService.ProcessInvoiceWebhookAsync(invoiceId, webhookEvent.TenantId, email);
                    }
                }
                else
                {
                    _logger.LogWarning($"[ProcessWebhookEvent] No customer emails found for invoice {invoiceId}");
                    LogToFile($"[ProcessWebhookEvent] No customer emails found for invoice {invoiceId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ProcessWebhookEvent] Error processing webhook event: {webhookEvent.EventType}");
                LogToFile($"[ProcessWebhookEvent] Error processing webhook event: {webhookEvent.EventType}: {ex.Message}");
            }
        }

        private string? ExtractInvoiceIdFromResourceUri(string resourceUri)
        {
            if (string.IsNullOrEmpty(resourceUri))
                return null;
            var parts = resourceUri.Split('/');
            return parts.Length > 0 ? parts[^1] : null;
        }
    }
} 