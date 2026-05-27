using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.AppRegistry;

public sealed class AppRegistryEditorModel
{
    public Guid? AppId { get; set; }

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string AppType { get; set; } = "WebsiteCms";

    [MaxLength(100)]
    public string? Subdomain { get; set; }

    [MaxLength(40)]
    public string Status { get; set; } = "Draft";

    [Range(1, 65535)]
    public int? Port { get; set; }
}
