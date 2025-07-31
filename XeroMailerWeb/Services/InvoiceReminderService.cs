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

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace XeroMailerWeb.Services;

public class InvoiceReminderService
{
    private readonly IConfiguration _config;
    private readonly EmailService _emailService;
    private readonly ILogger<InvoiceReminderService> _logger;
    private readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroInvoiceReminders.log");
    private readonly string _reminderStatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroInvoiceRemindersState.json");

    public InvoiceReminderService(IConfiguration config, EmailService emailService, ILogger<InvoiceReminderService> logger)
    {
        _config = config;
        _emailService = emailService;
        _logger = logger;
    }

    private void LogToFile(string message)
    {
        // Check if detailed logging is enabled
        var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
        if (!enableDetailedLogging)
            return;
            
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch { /* Ignore logging errors */ }
    }

    private Dictionary<string, DateTime> LoadReminderState()
    {
        if (!File.Exists(_reminderStatePath)) return new Dictionary<string, DateTime>();
        try
        {
            var json = File.ReadAllText(_reminderStatePath);
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new Dictionary<string, DateTime>();
        }
        catch
        {
            return new Dictionary<string, DateTime>();
        }
    }

    private void SaveReminderState(Dictionary<string, DateTime> state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_reminderStatePath, json);
    }

    public async Task RunInvoiceRemindersAsync()
    {
        try
        {
            LogToFile($"Starting invoice reminder job at {DateTime.UtcNow:O}...");
            // Load reminder state
            var tempXeroInvoiceReminderState = LoadReminderState();
            var XeroInvoiceReminderState = new Dictionary<string, DateTime>();
            // Check for app_XeroTokens.json in the base directory
            var tokenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroTokens.json");
            if (File.Exists(tokenPath))
            {
                LogToFile($"Found app_XeroTokens.json at: {tokenPath}");
            }
            else
            {
                LogToFile($"ERROR: app_XeroTokens.json not found at: {tokenPath}. Aborting.");
                return;
            }
            // 1. Authenticate with Xero (refresh token if needed)
            // Fix logger type for XeroService
            var xeroLogger = _logger as ILogger<XeroService> ?? new LoggerFactory().CreateLogger<XeroService>();
            var xeroService = new XeroService(_config, xeroLogger);
            var accessToken = await xeroService.GetValidAccessTokenAsync();
            // Get tenantId using XeroService
            var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
            var tenantId = await xeroService.GetTenantIdAsync(enableDetailedLogging ? _logPath : null);
            if (string.IsNullOrEmpty(tenantId))
            {
                LogToFile("No Xero tenant ID found. Aborting.");
                return;
            }
            //LogToFile($"Using tenantId: {tenantId}");

            // Fetch all outstanding invoices using XeroService
            List<JsonElement> invoices;
            try
            {
                invoices = await xeroService.GetOutstandingInvoicesAsync(tenantId);
            }
            catch (Exception ex)
            {
                LogToFile($"Failed to fetch invoices: {ex.Message}");
                return;
            }
            if (invoices.Count == 0)
            {
                LogToFile("No outstanding invoices found.");
                return;
            }

            // Fetch organisation details for the email footer
            var orgDetails = await xeroService.GetOrganisationDetailsAsync(tenantId);
            string orgName = orgDetails?.Name ?? orgDetails?.LegalName ?? "";
//            string orgAddress = string.Join(", ", new[] { orgDetails?.AddressLine1, orgDetails?.AddressLine2, orgDetails?.City, orgDetails?.Region, orgDetails?.PostalCode, orgDetails?.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string orgPhone = orgDetails?.PhoneNumber ?? "";
            string orgEmail = orgDetails?.Email ?? "";
            string orgWebsite = orgDetails?.Website ?? "";
            string orgAddressLine1 = (orgDetails?.AddressLine1 ?? "").Replace(",", "<br/>").Trim();
            string orgAddressLine2 = (orgDetails?.AddressLine2 ?? "").Replace(",", "<br/>").Trim();
            string orgCity = (orgDetails?.City ?? "").Replace(",", "<br/>").Trim();
            string orgRegion = (orgDetails?.Region ?? "").Replace(",", "<br/>").Trim();
            string orgPostalCode = (orgDetails?.PostalCode ?? "").Replace(",", "<br/>").Trim();
            string orgCountry = (orgDetails?.Country ?? "").Replace(",", "<br/>").Trim();

            // 3. Group by customer, filter overdue
            var remindersEnabled = _config.GetValue<bool>("XeroMailerWeb:InvoiceReminders", true);
            var remindAfterDays = _config.GetValue<int>("XeroMailerWeb:InvoiceRemindAfterDays", 7);
            var remindRepeatEveryDays = _config.GetValue<int>("XeroMailerWeb:InvoiceRemindRepeatEveryDays", 7);
            var now = DateTime.UtcNow.Date;

            var grouped = invoices
                .Where(inv => inv.GetProperty("AmountDue").GetDecimal() > 0)
                .GroupBy(inv => inv.GetProperty("Contact").GetProperty("ContactID").GetString());

            foreach (var group in grouped)
            {
                var firstInvoice = group.First();
                var invoiceId = firstInvoice.GetProperty("InvoiceID").GetString();
                // Fetch full invoice details to get up-to-date contact info
                var invoiceDetails = await xeroService.GetInvoiceDetailsAsync(tenantId, invoiceId ?? "");
                if (invoiceDetails == null)
                {
                    LogToFile($"Skipping group (could not fetch invoice details for {invoiceId}).");
                    continue;
                }
                var contact = invoiceDetails.Value.GetProperty("Contact");
                var contactName = contact.GetProperty("Name").GetString();
                var contactId = contact.GetProperty("ContactID").GetString();
                // Use ExtractContactEmails to get all emails
                var allEmails = XeroService.ExtractContactEmails(invoiceDetails.Value);
                if (allEmails.Count == 0)
                {
                    LogToFile($"Skipping {contactName} ({contactId}) - no contact emails found.");
                    continue;
                }
                var primaryEmail = allEmails[0];
                var ccEmails = allEmails.Skip(1).ToList();
                LogToFile($"Using emails for {contactName} ({contactId}): To={primaryEmail}, CC=[{string.Join(", ", ccEmails)}]");
                // Filter overdue invoices for this customer
                var overdueInvoices = group.Where(inv =>
                {
                    var dueDate = inv.TryGetProperty("DueDate", out var dueProp) ? ParseXeroDate(dueProp.GetString() ?? "") : (DateTime?)null;
                    if (dueDate == null) return false;
                    var daysOverdue = (now - dueDate.Value.Date).Days;
                    return daysOverdue >= remindAfterDays;
                }).ToList();
                if (overdueInvoices.Count == 0)
                    continue;
                // For each overdue invoice, check reminder frequency
                var invoicesToRemind = new List<JsonElement>();
                foreach (var inv in overdueInvoices)
                {
                    var invId = inv.GetProperty("InvoiceID").GetString();
                    var dueDate = ParseXeroDate(inv.GetProperty("DueDate").GetString() ?? "");
                    var daysOverdue = (now - dueDate.Date).Days;
                    if (daysOverdue < remindAfterDays) continue;
                    var lastSent = tempXeroInvoiceReminderState.TryGetValue(invId ?? "", out var last) ? last : (DateTime?)null;
                    var shouldSend = false;
                    if (lastSent == null)
                    {
                        // Never sent, send if overdue enough
                        shouldSend = daysOverdue >= remindAfterDays;
                    }
                    else
                    {
                        var daysSinceLast = (now - lastSent.Value.Date).Days;
                        // Send if enough days have passed since last reminder
                        shouldSend = daysSinceLast >= remindRepeatEveryDays;
                    }
                    if (shouldSend)
                    {
                        invoicesToRemind.Add(inv);
                        XeroInvoiceReminderState[invId ?? ""] = now;
                        LogToFile($"Will send reminder for invoice {invId} (overdue {daysOverdue} days, last sent: {(lastSent?.ToString("yyyy-MM-dd") ?? "never")})");
                    }
                    else
                    {
                        // Not sending, but keep the old date
                        XeroInvoiceReminderState[invId ?? ""] = lastSent ?? now;
                        LogToFile($"Skipping reminder for invoice {invId} (overdue {daysOverdue} days, last sent: {(lastSent?.ToString("yyyy-MM-dd") ?? "never")})");
                    }
                }
                if (invoicesToRemind.Count == 0)
                    continue;
                // Compose HTML email for only the invoices being reminded
                var currencyCode = group.First().GetProperty("CurrencyCode").GetString() ?? "GBP";
                var currencySymbol = CurrencySymbol(currencyCode);
                var totalOverdue = invoicesToRemind.Sum(inv => inv.GetProperty("AmountDue").GetDecimal());
                var formattedTotal = string.Format("{0}{1:N2}", currencySymbol, totalOverdue);
                var sb = new StringBuilder();
                sb.AppendLine($"<p>Dear {contact.GetProperty("FirstName").GetString()},</p>");
                sb.AppendLine("<p>The following invoices are overdue. Please bring your account up to date at your earliest convenience.</p>");
                sb.AppendLine("<table border='1' cellpadding='6' cellspacing='0' style='border-collapse:collapse; font-family:sans-serif; font-size:14px; min-width:400px;'>");
                sb.AppendLine("<thead><tr style='background:#f2f2f2;'><th>Invoice</th><th>Reference</th><th>Due Date</th><th>Amount Due</th></tr></thead>");
                sb.AppendLine("<tbody>");
                foreach (var inv in invoicesToRemind)
                {
                    var invoiceNumber = inv.GetProperty("InvoiceNumber").GetString();
                    var reference = inv.GetProperty("Reference").GetString();
                    var dueDate = ParseXeroDate(inv.GetProperty("DueDate").GetString() ?? "").ToString("yyyy-MM-dd");
                    var amountDue = inv.GetProperty("AmountDue").GetDecimal();
                    var formattedAmount = string.Format("{0}{1:N2}", currencySymbol, amountDue);
                    var invId = inv.GetProperty("InvoiceID").GetString();
                    var onlineUrl = await GetOnlineInvoiceUrlAsync(accessToken, tenantId, invId ?? "");
                    sb.AppendLine($"<tr>" +
                        $"<td><a href='{onlineUrl}' target='_blank' style='color:#028DDE;text-decoration:none;'>{invoiceNumber}</a></td>" +
                        $"<td>{reference}</td>" +
                        $"<td>{dueDate}</td>" +
                        $"<td style='text-align:right;'>{formattedAmount}</td>" +
                        "</tr>");
                }
                sb.AppendLine("</tbody>");
                sb.AppendLine("</table>");
                sb.AppendLine($"<p><strong>Total Overdue: {formattedTotal}</strong><br/><br/></p>");
                sb.AppendLine("<p>If you have already made payment, please disregard this reminder.</p>");
                // Send email (match EmailService signature)
                await _emailService.SendInvoiceReminderEmailAsync(
                    new List<string> { primaryEmail },
                    ccEmails,
                    "Overdue Invoice Reminder for " + contactName, // customSubject
                    sb.ToString(),
                    orgName,
                    orgPhone,
                    orgEmail,
                    orgWebsite,
                    orgAddressLine1,
                    orgAddressLine2,
                    orgCity,
                    orgRegion,
                    orgPostalCode,
                    orgCountry
                );
                LogToFile($"Sent reminder to {contactName} ({primaryEmail}) for {invoicesToRemind.Count} overdue invoices. Total: {formattedTotal}");
            }
            // Save reminder state
            SaveReminderState(XeroInvoiceReminderState);
            LogToFile($"Invoice reminder job complete at {DateTime.UtcNow:O}.");
        }
        catch (Exception ex)
        {
            LogToFile($"Error in invoice reminder job: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static string CurrencySymbol(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        return code.ToUpper() switch
        {
            "GBP" => "£",
            "USD" => "$",
            "EUR" => "€",
            "AUD" => "A$",
            "NZD" => "NZ$",
            "CAD" => "C$",
            "JPY" => "¥",
            "CHF" => "Fr.",
            "ZAR" => "R",
            "SGD" => "S$",
            "HKD" => "HK$",
            "CNY" => "¥",
            "INR" => "₹",
            _ => code
        };
    }

    private async Task<string?> GetOnlineInvoiceUrlAsync(string accessToken, string tenantId, string invoiceId)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var url = $"https://api.xero.com/api.xro/2.0/Invoices/{invoiceId}/OnlineInvoice";
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        request.Headers.Add("xero-tenant-id", tenantId);
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        LogToFile($" - OnlineInvoiceUrl: {json}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError($"Failed to get OnlineInvoiceUrl: {json}");
            LogToFile($"Failed to get OnlineInvoiceUrl: {json}");
            return null;
        }
        var doc = JsonDocument.Parse(json);
        string? foundUrl = FindOnlineInvoiceUrlRecursive(doc.RootElement);
        return foundUrl;
    }

    // Recursively search for OnlineInvoiceUrl in any depth
    private string? FindOnlineInvoiceUrlRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals("OnlineInvoiceUrl") && prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
                var found = FindOnlineInvoiceUrlRecursive(prop.Value);
                if (found != null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindOnlineInvoiceUrlRecursive(item);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static DateTime ParseXeroDate(string dateStr)
    {
        if (dateStr.StartsWith("/Date(") || dateStr.StartsWith("\\/Date("))
        {
            // Extract the milliseconds part
            var match = System.Text.RegularExpressions.Regex.Match(dateStr, @"\\?/Date\\?\((\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var ms))
            {
                // Xero uses milliseconds since Unix epoch
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }
            throw new FormatException("Invalid Xero /Date() format: " + dateStr);
        }
        // Fallback to normal parse
        return DateTime.Parse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }
} 