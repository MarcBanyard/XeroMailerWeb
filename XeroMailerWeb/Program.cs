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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XeroMailerWeb.Services;
using XeroMailerWeb.Models;

if (args.Contains("--reminderjob"))
{
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((hostingContext, config) =>
        {
            var basePath = AppContext.BaseDirectory;
            config.SetBasePath(basePath);
            config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        })
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<InvoiceReminderService>();
            services.AddSingleton<EmailService>();
        })
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        })
        .Build();

    var svc = host.Services.GetRequiredService<InvoiceReminderService>();
    await svc.RunInvoiceRemindersAsync();
    return;
}

// --- Original ASP.NET Core Web Host Startup ---
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add custom services
builder.Services.AddScoped<EmailService>();
builder.Services.AddSingleton<XeroService>();

// Register WebhookQueueService as singleton with the queue file path
builder.Services.AddSingleton<WebhookQueueService>(sp =>
    new WebhookQueueService(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroWebhookQueue.json")));

// Register WebhookProcessorService as hosted service using factory pattern
builder.Services.AddHostedService<WebhookProcessorService>(sp =>
    new WebhookProcessorService(
        sp.GetRequiredService<WebhookQueueService>(),
        sp.GetRequiredService<XeroService>(),
        sp.GetRequiredService<ILogger<WebhookProcessorService>>(),
        sp.GetRequiredService<IConfiguration>()
    ));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run(); 