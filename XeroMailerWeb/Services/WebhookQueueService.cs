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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XeroMailerWeb.Models;

namespace XeroMailerWeb.Services
{
    public class WebhookQueueService
    {
        private readonly string _queueFilePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public WebhookQueueService(string queueFilePath)
        {
            _queueFilePath = queueFilePath;
        }

        public async Task EnqueueAsync(XeroEvent webhookEvent)
        {
            await _lock.WaitAsync();
            try
            {
                var queue = await ReadQueueAsync();
                // Deduplicate by ResourceId and EventDateUtc
                if (!queue.Any(e => e.ResourceId == webhookEvent.ResourceId && e.EventDateUtc == webhookEvent.EventDateUtc))
                {
                    queue.Add(webhookEvent);
                    await WriteQueueAsync(queue);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<List<XeroEvent>> GetAllAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return await ReadQueueAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task RemoveAsync(XeroEvent webhookEvent)
        {
            await _lock.WaitAsync();
            try
            {
                var queue = await ReadQueueAsync();
                // Remove by matching ResourceId and EventDateUtc for uniqueness
                var toRemove = queue.FirstOrDefault(e => e.ResourceId == webhookEvent.ResourceId && e.EventDateUtc == webhookEvent.EventDateUtc);
                if (toRemove != null)
                {
                    queue.Remove(toRemove);
                    await WriteQueueAsync(queue);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<List<XeroEvent>> ReadQueueAsync()
        {
            if (!File.Exists(_queueFilePath))
                return new List<XeroEvent>();
            var json = await File.ReadAllTextAsync(_queueFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new List<XeroEvent>();
            return JsonSerializer.Deserialize<List<XeroEvent>>(json) ?? new List<XeroEvent>();
        }

        private async Task WriteQueueAsync(List<XeroEvent> queue)
        {
            var json = JsonSerializer.Serialize(queue, new JsonSerializerOptions { WriteIndented = true });
            try
            {
                Console.WriteLine($"[WebhookQueueService] Writing queue to: {_queueFilePath}");
                await File.WriteAllTextAsync(_queueFilePath, json);
                Console.WriteLine($"[WebhookQueueService] Successfully wrote queue file. Queue count: {queue.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebhookQueueService] Error writing queue file: {ex.Message}");
            }
        }
    }
} 