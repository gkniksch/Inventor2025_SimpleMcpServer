using System.Text.Json.Serialization;

namespace InventorMcpServer.Models;

/// <summary>
/// Deserialized representation of the optional filterJson parameter in query_geometry.
/// </summary>
public sealed class GeometryFilterOptions
{
    [JsonPropertyName("normal")]
    public double[]? Normal { get; set; }

    [JsonPropertyName("axis")]
    public double[]? Axis { get; set; }

    [JsonPropertyName("min_area")]
    public double? MinArea { get; set; }

    [JsonPropertyName("max_area")]
    public double? MaxArea { get; set; }

    [JsonPropertyName("min_radius")]
    public double? MinRadius { get; set; }

    [JsonPropertyName("max_radius")]
    public double? MaxRadius { get; set; }

    [JsonPropertyName("min_length")]
    public double? MinLength { get; set; }

    [JsonPropertyName("max_length")]
    public double? MaxLength { get; set; }

    [JsonPropertyName("curve_type")]
    public string? CurveType { get; set; }

    [JsonPropertyName("body_name")]
    public string? BodyName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("grounded")]
    public bool? Grounded { get; set; }

    [JsonPropertyName("suppressed")]
    public bool? Suppressed { get; set; }

    [JsonPropertyName("set_name")]
    public string? SetName { get; set; }

    [JsonPropertyName("attribute_name")]
    public string? AttributeName { get; set; }

    [JsonPropertyName("attribute_value")]
    public string? AttributeValue { get; set; }

    [JsonPropertyName("tolerance")]
    public double Tolerance { get; set; } = 0.02;

    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 100;
}
