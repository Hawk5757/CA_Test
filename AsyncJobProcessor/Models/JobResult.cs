using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AsyncJobProcessor.Models;

public class JobResult
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; set; }
    
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public JobStatus Status { get; set; }

    [StringLength(500)]
    public string Message { get; set; } = string.Empty;
    public object? Payload { get; set; }
    [StringLength(500)]
    public string? ErrorMessage { get; set; }
}