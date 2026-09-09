namespace Hanime1Downloader.CSharp.Models;

public sealed class SearchFilterOptions
{
    public string Genre { get; set; } = string.Empty;
    public string Sort { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool Broad { get; set; }

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(Genre) ||
        !string.IsNullOrWhiteSpace(Sort) ||
        !string.IsNullOrWhiteSpace(Date) ||
        !string.IsNullOrWhiteSpace(Duration) ||
        Tags.Count > 0 ||
        Broad;
}
