﻿#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using Playnite.SDK;
using Playnite.SDK.Metadata;
using Playnite.SDK.Plugins;
using ScreenScraperMetadata.Models;
using ScreenScraperMetadata.Services;

namespace ScreenScraperMetadata
{
    public class ScreenScraperMetadataProvider : OnDemandMetadataProvider
    {
        private readonly MetadataRequestOptions options;
        private readonly ScreenScraperMetadata plugin;
        private readonly List<string> searchRegions;
        private readonly ScreenScraperServiceClient service;
        private readonly ScreenScraperMetadataSettings settings;
        private readonly Jeu? ssGameInfo;

        public ScreenScraperMetadataProvider(MetadataRequestOptions options, ScreenScraperMetadata plugin,
            ScreenScraperMetadataSettings settings)
        {
            this.options = options;
            this.plugin = plugin;
            this.settings = settings;
            service = new ScreenScraperServiceClient(settings);
            ssGameInfo = service.GetJeuInfo(options.GameData)?.response.jeu;
            AvailableFields = GetAvailableFields();
            searchRegions = GetRegionsToCheck().ToList();
        }

        public override List<MetadataField> AvailableFields { get; }

        public override MetadataFile? GetCoverImage()
        {
            var url = FindMediaItem("box-2D");
            return url != null ? new MetadataFile(url) : base.GetCoverImage();
        }

        public override MetadataFile? GetBackgroundImage()
        {
            var url = FindMediaItem("fanart");
            if (settings.ShouldUseScreenshots && url == null)
            {
                var imageFileOptions = FindMediaItems("ss")
                    .Concat(FindMediaItems("sstitle"))
                    .Select(mediaUrl => new ImageFileOption(mediaUrl))
                    .ToList();
                if (!imageFileOptions.HasItems()) return base.GetBackgroundImage();
                url = options.IsBackgroundDownload || imageFileOptions.Count == 1
                    ? imageFileOptions.First().Path
                    : plugin.PlayniteApi.Dialogs.ChooseImageFile(imageFileOptions, "Choose background image")?.Path;
            }

            return url != null ? new MetadataFile(url) : base.GetBackgroundImage();
        }

        public override string? GetDescription()
        {
            var synopsis = ssGameInfo?.synopsis?.Find(
                item => item.langue == "en"
            );
            var text = synopsis?.text;
            return HttpUtility.HtmlDecode(text);
        }

        public override List<string>? GetDevelopers()
        {
            var developer = ssGameInfo?.developpeur?.text;
            if (developer != null)
                return new List<string>
                {
                    developer
                };
            return base.GetDevelopers();
        }

        public override List<string?>? GetGenres()
        {
            return ssGameInfo?.genres?.Select(
                    item => item.noms.Find(nom => nom.langue == "en")?.text
                )
                .Where(genre => !string.IsNullOrEmpty(genre))
                .ToList();
        }

        public override MetadataFile? GetIcon()
        {
            var url = FindMediaItem("wheel") ?? FindMediaItem("wheel-hd");
            return url != null ? new MetadataFile(url) : null;
        }

        public override string? GetName()
        {
            var found = searchRegions.Select(region =>
                    ssGameInfo?.noms.Find(nom =>
                    {
                        LogManager.GetLogger().Info("region: " + region + " item region: " + nom.langue);
                        return region.Equals(nom.region);
                    })
                ).First()
                ?.text;
            return found;
        }

        public override string? GetPlatform()
        {
            var platformId = ssGameInfo?.systeme.id;
            return platformId != null ? service.GetPlatformNameById(platformId) : base.GetPlatform();
        }

        public override List<string>? GetPublishers()
        {
            var publisher = ssGameInfo?.editeur?.text;
            return publisher != null ? new List<string> { publisher } : base.GetDevelopers();
        }

        public override DateTime? GetReleaseDate()
        {
            var region = GetGameRegion();
            var dateResult = ssGameInfo?.dates?.Find(dateInfo =>
                dateInfo.region == region
            ) ?? ssGameInfo?.dates?.FirstOrDefault();

            if (dateResult == null) return base.GetReleaseDate();

            try
            {
                return DateTime.ParseExact(
                    dateResult.text,
                    dateResult.text?.Length == 4 ? "yyyy" : "yyyy-MM-dd",
                    null
                );
            }
            catch (Exception)
            {
                return base.GetReleaseDate();
            }
        }

        public override string? GetAgeRating()
        {
            var ageRatingPreference = plugin.PlayniteApi.ApplicationSettings.AgeRatingOrgPriority;
            var dateResult = ssGameInfo?.classifications?.Find(rating =>
                rating.type == ageRatingPreference.ToString()
            ) ?? ssGameInfo?.classifications?.FirstOrDefault();
            if (dateResult != null) return dateResult.type + " " + dateResult.text;
            return base.GetAgeRating();
        }

        private IEnumerable<string> GetRegionsToCheck()
        {
            var regionsStr = settings.RegionPreferences;
            var preferredRegions = regionsStr.Split(',').ToList();
            var regions = new SortedSet<string> { GetGameRegion() };
            regions.UnionWith(preferredRegions);
            return regions;
        }

        private List<MetadataField> GetAvailableFields()
        {
            var metadataFields = new List<MetadataField>();
            if (ssGameInfo == null) return metadataFields;

            metadataFields.Add(MetadataField.Name);
            if (ssGameInfo.genres?.HasItems() == true) metadataFields.Add(MetadataField.Genres);

            if (ssGameInfo.dates?.HasItems() == true) metadataFields.Add(MetadataField.ReleaseDate);

            if (ssGameInfo.developpeur != null) metadataFields.Add(MetadataField.Developers);

            if (ssGameInfo.editeur != null) metadataFields.Add(MetadataField.Publishers);

            if (ssGameInfo.synopsis?.HasItems() == true) metadataFields.Add(MetadataField.Description);

            if (ssGameInfo.medias.HasItems())
            {
                metadataFields.Add(MetadataField.CoverImage);
                metadataFields.Add(MetadataField.BackgroundImage);
                metadataFields.Add(MetadataField.Icon);
            }

            if (ssGameInfo.classifications?.HasItems() == true) metadataFields.Add(MetadataField.AgeRating);

            metadataFields.Add(MetadataField.Platform);

            return metadataFields;
        }

        private string GetGameRegion()
        {
            if (!HasRomFile()) return "wor";

            const string pattern = @"\((.+?)\)";
            var filename = GetRomFileName();
            foreach (Match m in Regex.Matches(filename, pattern))
            {
                var regionCandidate = m.Groups[0].Value;

                switch (regionCandidate)
                {
                    case "A":
                        return "au"; //Australia
                    case "B":
                        return "br"; //Brazil
                    case "C":
                        return "cn"; //China
                    case "E":
                    case "EU":
                        return "eu"; //Europe
                    case "FN":
                        return "fi"; //Finland
                    case "F":
                        return "fr"; //France
                    case "GR":
                        return "gr"; //Greece
                    case "I":
                        return "it"; //Italy
                    case "J":
                    case "1":
                        return "jp"; //Japan
                    case "K":
                        return "kr"; //Korea
                    case "NL":
                        return "nl"; //Netherlands
                    case "S":
                        return "sp"; //Spain
                    case "SW":
                        return "se"; //Sweden
                    case "U":
                    case "4":
                    case "USA":
                    case "US":
                        return "us"; //USA
                    case "UK":
                        return "uk"; //UK
                }
            }

            return "wor";
        }

        private string? FindMediaItem(string type)
        {
            var found = searchRegions.Select(region =>
                    ssGameInfo?.medias.Find(item =>
                        type.Equals(item.type)
                        && (region.Equals(item.region) || item.region == null)
                    ))
                .FirstOrDefault(mediaItem => mediaItem != null);
            return found?.url ?? ssGameInfo?.medias.Find(item => type.Equals(item.type))?.url;
        }

        private IEnumerable<string> FindMediaItems(string type)
        {
            return searchRegions.Select(region =>
                    ssGameInfo?.medias.Find(item =>
                        type.Equals(item.type)
                        && (region.Equals(item.region) || item.region == null)
                    ))
                .Where(item => item != null)
                .Select(item => item!.url);
        }

        private bool HasRomFile()
        {
            return File.Exists(options.GameData.GameImagePath);
        }

        private string GetRomFileName()
        {
            return Path.GetFileName(options.GameData.GameImagePath);
        }
    }
}