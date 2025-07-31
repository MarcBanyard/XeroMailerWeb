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

using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace XeroMailerWeb.Services;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task SendInvoiceEmailAsync(
        List<string> toEmails,
        List<string>? ccEmails,
        byte[]? pdfBytes,
        string pdfFileName,
        string? invoiceUrl = null,
        string? customSubject = null,
        string? orgName = null,
        string? orgAddress = null,
        string? orgPhone = null,
        string? orgEmail = null,
        string? orgWebsite = null,
        string? firstName = null,
        string? invoiceNumber = null,
        string? reference = null,
        string? orgAddressLine1 = null,
        string? orgAddressLine2 = null,
        string? orgCity = null,
        string? orgRegion = null,
        string? orgPostalCode = null,
        string? orgCountry = null)
    {
        _config["Xero:LastInvoicePdfName"] = pdfFileName; // For possible future use
        
        // Check if detailed logging is enabled
        var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
        
        // Log the details
        if (enableDetailedLogging)
        {
            Console.WriteLine($"[EmailService] Sending invoice to {string.Join(", ", toEmails)} (PDF attached: {pdfBytes != null})");
        }
        
        var credential = new ClientSecretCredential(
            _config["Entra:DirectoryTenantId"],
            _config["Entra:AppRegClientId"],
            _config["Entra:AppRegClientSecret"]);

        var graphClient = new GraphServiceClient(credential);
        var hasPdf = pdfBytes != null;
        var hasLink = !string.IsNullOrEmpty(invoiceUrl);
        string bodyContent;
        if (hasPdf && hasLink)
        {
            bodyContent =
                "Hi " + (string.IsNullOrWhiteSpace(firstName) ? "there" : firstName) + ",<br/>" +
                "Please find attached invoice " + (invoiceNumber ?? pdfFileName) +
                (string.IsNullOrWhiteSpace(reference) ? "" : " for " + reference) + ".<br/><br/>" +
                "You can view this invoice online:<br/>" +
                "<a href=\"" + invoiceUrl + "\" target=\"_blank\" style=\"color:#028DDE\">" + invoiceUrl + "</a><br/>" +
                "Where you can print a PDF, export a CSV, or create a free login and view your outstanding bills.<br/><br/>" +
                "If you have any questions, please let us know.<br/><br/>";
        }
        else if (hasLink)
        {
            bodyContent =
                "Hi " + (string.IsNullOrWhiteSpace(firstName) ? "there" : firstName) + ",<br/>" +
                "You can download your invoice online here:<br/>" +
                "<a href=\"" + invoiceUrl + "\" target=\"_blank\" style=\"color:#028DDE\">" + invoiceUrl + "</a><br/>" +
                "Where you can print a PDF, export a CSV, or create a free login and view your outstanding bills.<br/><br/>" +
                "If you have any questions, please let us know.<br/><br/>";
        }
        else if (hasPdf)
        {
            bodyContent =
                "Hi " + (string.IsNullOrWhiteSpace(firstName) ? "there" : firstName) + ",<br/>" +
                "Please find attached invoice " + (invoiceNumber ?? pdfFileName) +
                (string.IsNullOrWhiteSpace(reference) ? "" : " for " + reference) + ".<br/><br/>" +
                "If you have any questions, please let us know.<br/><br/>";
        }
        else
        {
            bodyContent = "Your invoice has been sent. Please contact us if you need access to view or download the invoice.";
        }

        var includeCompanyInfo = _config.GetValue<bool>("XeroMailerWeb:IncludeCompanyInfoInEmail", true);
        var includeCompanyCountryInEmail = _config.GetValue<bool>("XeroMailerWeb:IncludeCompanyCountryInEmail", true);
        if (includeCompanyInfo)
        {
            bodyContent += "<br/>Thanks,<br/>";
            if (!string.IsNullOrEmpty(orgName)) bodyContent += orgName + "<br/>";
            if (!string.IsNullOrEmpty(orgAddressLine1)) bodyContent += orgAddressLine1 + "<br/>";
            if (!string.IsNullOrEmpty(orgAddressLine2)) bodyContent += orgAddressLine2 + "<br/>";
            if (!string.IsNullOrEmpty(orgCity)) bodyContent += orgCity + "<br/>";
            if (!string.IsNullOrEmpty(orgRegion)) bodyContent += orgRegion + "<br/>";
            if (!string.IsNullOrEmpty(orgPostalCode)) bodyContent += orgPostalCode + "<br/>";
            if (includeCompanyCountryInEmail) {
                if (!string.IsNullOrEmpty(orgCountry)) bodyContent += orgCountry + "<br/>";
            }
            if (!string.IsNullOrEmpty(orgPhone)) bodyContent += "<br/>Phone: " + orgPhone + "<br/>";
            if (!string.IsNullOrEmpty(orgEmail)) bodyContent += "Email: <a href='mailto:" + orgEmail + "'>" + orgEmail + "</a><br/>";
            if (!string.IsNullOrEmpty(orgWebsite))
            {
                string displayWebsite = orgWebsite;
                if (displayWebsite.StartsWith("http://")) displayWebsite = displayWebsite.Substring(7);
                else if (displayWebsite.StartsWith("https://")) displayWebsite = displayWebsite.Substring(8);
                bodyContent += "Website: <a href='" + orgWebsite + "'>" + displayWebsite + "</a><br/>";
            }
        }

        var message = new Message
        {
            Subject = customSubject ?? "Your Invoice",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = bodyContent
            },
            ToRecipients = toEmails.Select(e => new Recipient { EmailAddress = new EmailAddress { Address = e } }).ToList(),
            CcRecipients = ccEmails != null ? ccEmails.Select(e => new Recipient { EmailAddress = new EmailAddress { Address = e } }).ToList() : null
        };

        // Add BCC to shared mailbox if enabled
        var bccSharedMailbox = _config.GetValue<bool>("XeroMailerWeb:BccSharedMailbox", false);
        if (bccSharedMailbox)
        {
            var sharedMailbox = _config["Entra:SharedMailbox"] ?? _config["Xero:SharedMailbox"];
            if (!string.IsNullOrWhiteSpace(sharedMailbox))
            {
                message.BccRecipients = new List<Recipient>
                {
                    new Recipient
                    {
                        EmailAddress = new EmailAddress
                        {
                            Address = sharedMailbox
                        }
                    }
                };
            }
        }

        if (hasPdf)
        {
            message.Attachments = new List<Attachment>
            {
                new FileAttachment
                {
                    Name = pdfFileName,
                    ContentType = "application/pdf",
                    ContentBytes = pdfBytes
                }
            };
        }

        // Try to use shared mailbox if configured, otherwise use current user
        var userOrMailbox = _config["Entra:SharedMailbox"] ?? "me";
        
        // Use the SendMail endpoint instead of creating a message in the mailbox
        var sendMailPostRequestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };
        
        await graphClient.Users[userOrMailbox].SendMail.PostAsync(sendMailPostRequestBody);
        
        if (enableDetailedLogging)
        {
            Console.WriteLine($"[EmailService] Invoice email sent successfully to {string.Join(", ", toEmails)}");
        }
    }

    public async Task SendInvoiceReminderEmailAsync(
        List<string> toEmails,
        List<string>? ccEmails,
        string? customSubject = null,
        string? bodyContent = null,
        string? orgName = null,
        string? orgPhone = null,
        string? orgEmail = null,
        string? orgWebsite = null,
        string? orgAddressLine1 = null,
        string? orgAddressLine2 = null,
        string? orgCity = null,
        string? orgRegion = null,
        string? orgPostalCode = null,
        string? orgCountry = null)
    {
        // Check if detailed logging is enabled
        var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
        
        // Log the details
        if (enableDetailedLogging)
        {
            Console.WriteLine($"[EmailService] Sending invoice reminder to {string.Join(", ", toEmails)}");
        }
        
        var credential = new ClientSecretCredential(
            _config["Entra:DirectoryTenantId"],
            _config["Entra:AppRegClientId"],
            _config["Entra:AppRegClientSecret"]);

        var graphClient = new GraphServiceClient(credential);
        if (string.IsNullOrEmpty(bodyContent))
        {
            bodyContent = "Your invoice has been sent. Please contact us if you need access to view or download the invoice.";
        }

        var includeCompanyInfo = _config.GetValue<bool>("XeroMailerWeb:IncludeCompanyInfoInEmail", true);
        var includeCompanyCountryInEmail = _config.GetValue<bool>("XeroMailerWeb:IncludeCompanyCountryInEmail", true);
        if (includeCompanyInfo)
        {
            bodyContent += "<br/>Thanks,<br/>";
            if (!string.IsNullOrEmpty(orgName)) bodyContent += orgName + "<br/>";
            if (!string.IsNullOrEmpty(orgAddressLine1)) bodyContent += orgAddressLine1 + "<br/>";
            if (!string.IsNullOrEmpty(orgAddressLine2)) bodyContent += orgAddressLine2 + "<br/>";
            if (!string.IsNullOrEmpty(orgCity)) bodyContent += orgCity + "<br/>";
            if (!string.IsNullOrEmpty(orgRegion)) bodyContent += orgRegion + "<br/>";
            if (!string.IsNullOrEmpty(orgPostalCode)) bodyContent += orgPostalCode + "<br/>";
            if (includeCompanyCountryInEmail) {
                if (!string.IsNullOrEmpty(orgCountry)) bodyContent += orgCountry + "<br/>";
            }
            if (!string.IsNullOrEmpty(orgPhone)) bodyContent += "<br/>Phone: " + orgPhone + "<br/>";
            if (!string.IsNullOrEmpty(orgEmail)) bodyContent += "Email: <a href='mailto:" + orgEmail + "'>" + orgEmail + "</a><br/>";
            if (!string.IsNullOrEmpty(orgWebsite))
            {
                string displayWebsite = orgWebsite;
                if (displayWebsite.StartsWith("http://")) displayWebsite = displayWebsite.Substring(7);
                else if (displayWebsite.StartsWith("https://")) displayWebsite = displayWebsite.Substring(8);
                bodyContent += "Website: <a href='" + orgWebsite + "'>" + displayWebsite + "</a><br/>";
            }
        }

        var message = new Message
        {
            Subject = customSubject ?? "Your Invoice",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = bodyContent
            },
            ToRecipients = toEmails.Select(e => new Recipient { EmailAddress = new EmailAddress { Address = e } }).ToList(),
            CcRecipients = ccEmails != null ? ccEmails.Select(e => new Recipient { EmailAddress = new EmailAddress { Address = e } }).ToList() : null
        };

        // Add BCC to shared mailbox if enabled
        var bccSharedMailbox = _config.GetValue<bool>("XeroMailerWeb:BccSharedMailbox", false);
        if (bccSharedMailbox)
        {
            var sharedMailbox = _config["Entra:SharedMailbox"] ?? _config["Xero:SharedMailbox"];
            if (!string.IsNullOrWhiteSpace(sharedMailbox))
            {
                message.BccRecipients = new List<Recipient>
                {
                    new Recipient
                    {
                        EmailAddress = new EmailAddress
                        {
                            Address = sharedMailbox
                        }
                    }
                };
            }
        }

        // Try to use shared mailbox if configured, otherwise use current user
        var userOrMailbox = _config["Entra:SharedMailbox"] ?? "me";
        
        // Use the SendMail endpoint instead of creating a message in the mailbox
        var sendMailPostRequestBody = new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };
        
        await graphClient.Users[userOrMailbox].SendMail.PostAsync(sendMailPostRequestBody);
        
        if (enableDetailedLogging)
        {
            Console.WriteLine($"[EmailService] Invoice email sent successfully to {string.Join(", ", toEmails)}");
        }
    }
} 