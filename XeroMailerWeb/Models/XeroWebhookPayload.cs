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

using System.Text.Json.Serialization;

namespace XeroMailerWeb.Models;

public class XeroWebhookPayload
{
    [JsonPropertyName("events")]
    public List<XeroEvent> Events { get; set; } = new();
    
    [JsonPropertyName("firstEventSequence")]
    public int FirstEventSequence { get; set; }
    
    [JsonPropertyName("lastEventSequence")]
    public int LastEventSequence { get; set; }
    
    [JsonPropertyName("entropy")]
    public string Entropy { get; set; } = string.Empty;
}

public class XeroEvent
{
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; set; } = string.Empty;
    
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;
    
    [JsonPropertyName("eventCategory")]
    public string EventCategory { get; set; } = string.Empty;
    
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;
    
    [JsonPropertyName("eventDateUtc")]
    public DateTime EventDateUtc { get; set; }
    
    [JsonPropertyName("resourceUrl")]
    public string ResourceUri { get; set; } = string.Empty;
} 