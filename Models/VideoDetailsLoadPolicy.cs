namespace Hanime1Downloader.CSharp.Models;

public static class VideoDetailsLoadPolicy
{
    public static VideoDetailsLoadOptions ForVisibility(VideoDetailsVisibilitySettings visibility, bool includeSources = true)
    {
        ArgumentNullException.ThrowIfNull(visibility);

        var options = VideoDetailsLoadOptions.Basic;
        if (includeSources)
        {
            options |= VideoDetailsLoadOptions.Sources;
        }

        if (visibility.Cover)
        {
            options |= VideoDetailsLoadOptions.Cover;
        }

        if (visibility.UploadDate || visibility.Likes || visibility.Views || visibility.Duration)
        {
            options |= VideoDetailsLoadOptions.Meta;
        }

        if (visibility.Tags)
        {
            options |= VideoDetailsLoadOptions.Tags;
        }

        if (visibility.RelatedVideos)
        {
            options |= VideoDetailsLoadOptions.RelatedVideos;
        }

        return options;
    }
}
