using System.ComponentModel;

namespace Shared.Contracts;

/// <summary>
/// TODO Будет использоваться в системном туллинге
/// </summary>
public class DiffEdit
{
    /// <summary>
    /// Начальная линия (с tolerance)
    /// </summary>
    [Description("Approximate start line or null")]
    public int? ApproximateLine { get; set; } = null;

    [Description("Unique fragment of code")]
    public string OldStr { get; set; } = string.Empty;

    [Description("New fragment of code")]
    public string NewStr { get; set; } = string.Empty;
}
