﻿// Copyright 2018 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using NuGet.Versioning;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Utilities;

// ReSharper disable ArgumentsStyleLiteral
namespace Nuke.Common.ChangeLog
{

    public static class ChangelogTasks
    {
        [Pure]
        public static IReadOnlyList<ReleaseNotes> ReadReleaseNotes(string changelogFile)
        {
            var lines = TextTasks.ReadAllLines(changelogFile).ToList();
            var releaseSections = GetReleaseSections(lines).ToList();
            
            ControlFlow.Assert(releaseSections.Any(), "Changelog should have at least one release note section");
            return releaseSections.Select(Parse).ToList().AsReadOnly();
            
            ReleaseNotes Parse(ReleaseSection section)
            {
                var releaseNotes = lines
                    .Skip(section.StartIndex + 1)
                    .Take(section.EndIndex - section.StartIndex)
                    .ToList()
                    .AsReadOnly();
                
                return NuGetVersion.TryParse(section.Caption, out var version)
                    ? new ReleaseNotes(version, releaseNotes, section.StartIndex, section.EndIndex)
                    : new ReleaseNotes(releaseNotes, section.StartIndex, section.EndIndex);
            }
        }

        [Pure]
        public static ChangeLog ReadChangelog(string changelogFile)
        {
            var releaseNotes = ReadReleaseNotes(changelogFile);
            var unreleased = releaseNotes.Where(x => x.Unreleased).ToArray();

            if (unreleased.Length > 0)
            {
                ControlFlow.Assert(unreleased.Length == 1, "Changelog should have only one draft section.");
                return new ChangeLog(changelogFile, unreleased.First(), releaseNotes);
            }
            ControlFlow.Assert(releaseNotes.Count(x => !x.Unreleased) >= 1, "Changelog should have at lease one released version section.");
            return new ChangeLog(changelogFile, releaseNotes);
        }
        
        public static void FinalizeChangelog(ChangeLog changeLogFile, NuGetVersion tag)
        {
            Logger.Info($"Finalizing {PathConstruction.GetRootRelativePath(changeLogFile.Path)} for '{tag}'...");

            var unreleasedNotes = changeLogFile.Unreleased;
            var releaseNotes = changeLogFile.ReleaseNotes;
            var lastReleased = changeLogFile.LatestVersion;

            ControlFlow.Assert(unreleasedNotes != null, "Changelog should have draft section.");
            ControlFlow.Assert(releaseNotes.Any(x => x.Version != null && x.Version.Equals(tag)), $"Tag '{tag}' already exists.");
            ControlFlow.Assert(tag.CompareTo(lastReleased.Version) > 0, $"Tag '{tag}' is not greater compared to last tag '{lastReleased.Version}'.");

            var path = changeLogFile.Path;
            
            var content = TextTasks.ReadAllLines(path).ToList();
            
            content.Insert(unreleasedNotes.StartIndex + 1, string.Empty);
            content.Insert(unreleasedNotes.EndIndex + 2, $"## [{tag}] / {DateTime.Now:yyyy-MM-dd}");
            content.Add(string.Empty);

            TextTasks.WriteAllLines(path, content);
        }
        
        [Pure]
        public static IEnumerable<string> ExtractChangelogSectionNotes(string changelogFile, string tag = null)
        {
            var content = TextTasks.ReadAllLines(changelogFile).ToList();
            var sections = GetReleaseSections(content);
            var section = tag == null
                ? sections.First(x => x.StartIndex < x.EndIndex)
                : sections.First(x => x.Caption.EqualsOrdinalIgnoreCase(tag)).NotNull($"Could not find release section for '{tag}'.");

            return content
                .Skip(section.StartIndex + 1)
                .Take(section.EndIndex - section.StartIndex);
        }

        public static void FinalizeChangelog(string changelogFile, string tag, GitRepository repository = null)
        {
            Logger.Info($"Finalizing {PathConstruction.GetRootRelativePath(changelogFile)} for '{tag}'...");

            var content = TextTasks.ReadAllLines(changelogFile).ToList();
            var sections = GetReleaseSections(content).ToList();
            var firstSection = sections.First();
            var secondSection = sections.Skip(1).FirstOrDefault();

            ControlFlow.Assert(firstSection.Caption.All(char.IsLetter), "Cannot find a draft section.");
            ControlFlow.Assert(sections.All(x => !x.Caption.EqualsOrdinalIgnoreCase(tag)), $"Tag '{tag}' already exists.");
            ControlFlow.Assert(firstSection.EndIndex > firstSection.StartIndex,
                $"Draft section '{firstSection.Caption}' does not contain any information.");
            ControlFlow.Assert(secondSection == null || NuGetVersion.Parse(tag).CompareTo(NuGetVersion.Parse(secondSection.Caption)) > 0,
                $"Tag '{tag}' is not greater compared to last tag '{secondSection?.Caption}'.");

            content.Insert(firstSection.StartIndex + 1, string.Empty);
            content.Insert(firstSection.StartIndex + 2, $"## [{tag}] / {DateTime.Now:yyyy-MM-dd}");

            if (repository.IsGitHubRepository())
            {
                sections = GetReleaseSections(content).ToList();
                firstSection = sections.First();
                var lastSection = sections.Last();

                content.RemoveRange(lastSection.EndIndex + 1, content.Count - lastSection.EndIndex - 1);

                content.Add(string.Empty);
                content.Add($"[{firstSection.Caption}]: {repository}/compare/{tag}...HEAD");
                for (var i = 1; i + 1 < sections.Count; i++)
                    content.Add($"[{sections[i].Caption}]: {repository}/compare/{sections[i + 1].Caption}...{sections[i].Caption}");
                content.Add($"[{lastSection.Caption}]: {repository}/tree/{lastSection.Caption}");
            }

            content.Add(string.Empty);

            TextTasks.WriteAllLines(changelogFile, content);
        }

        private static IEnumerable<ReleaseSection> GetReleaseSections(List<string> content)
        {
            bool IsReleaseHead(string str)
                => str.StartsWith("## ");

            bool IsReleaseContent(string str)
                => str.StartsWith("###") || str.Trim().StartsWith("-");

            string GetCaption(string str)
                => str
                    .TrimStart('#', ' ', '[')
                    .Split(' ')
                    .First()
                    .TrimEnd(']');

            int GetTrimmedEndIndex(int endIndex)
            {
                for (var i = endIndex; !IsReleaseHead(content[i]); i--)
                {
                    if (!string.IsNullOrWhiteSpace(content[i]))
                        return i;
                }

                return endIndex - 1;
            }

            var index = content.FindIndex(IsReleaseHead);
            while (index < content.Count)
            {
                var line = content[index];
                if (!IsReleaseHead(line))
                {
                    index++;
                    continue;
                }

                var caption = GetCaption(line);
                var endIndex = content.FindIndex(index + 1, x => IsReleaseHead(x) || !IsReleaseContent(x));
                if (endIndex == -1)
                    endIndex = content.Count - 1;

                var releaseData =
                    new ReleaseSection
                    {
                        Caption = caption,
                        StartIndex = index,
                        EndIndex = GetTrimmedEndIndex(endIndex)
                    };

                yield return releaseData;
                Logger.Trace($"Found section '{caption}' [{index}-{releaseData.EndIndex}].");

                index = releaseData.EndIndex + 1;
            }
        }

        [DebuggerDisplay("{" + nameof(Caption) + "} [{" + nameof(StartIndex) + "}-{" + nameof(EndIndex) + "}]")]
        private class ReleaseSection
        {
            public string Caption;
            public int StartIndex;
            public int EndIndex;
        }
    }
}
