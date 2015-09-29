﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.VisualStudio;
using NuGet.Versioning;

namespace NuGet.PackageManagement.UI
{
    internal class InstalledLoader : ILoader
    {
        public string LoadingMessage
        {
            get;
            private set;
        }

        private readonly string LogEntrySource = "NuGet Package Manager";

        private SourceRepository _sourceRepository;
        private string _searchText;
        private List<NuGetProject> _projects;
        private Dictionary<string, List<NuGetVersion>> _installedPackages;
        private bool _includePrerelease;
        private InstalledPackagesLoader _installedPackageLoader;
        private UIMetadataResource _localResource;

        public void SetOptions(
            bool includePrerelease,
            SourceRepository sourceRepository,
            UIMetadataResource localResource,
            IEnumerable<NuGetProject> projects,
            InstalledPackagesLoader installedPackageLoader,
            string searchText)
        {
            _includePrerelease = includePrerelease;
            _localResource = localResource;
            _sourceRepository = sourceRepository;
            _searchText = searchText;
            _projects = projects.ToList();
            _installedPackageLoader = installedPackageLoader;            

            LoadingMessage = string.IsNullOrWhiteSpace(searchText) ?
                Resources.Text_Loading :
                string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Text_Searching,
                    searchText);
        }

        private async Task<SearchResult> SearchInstalledAsync(int startIndex, CancellationToken cancellationToken)
        {
            if (_installedPackages == null)
            {
                _installedPackages = await _installedPackageLoader.GetInstalledPackages();
            }

            var installedPackages = _installedPackages.Where(
                kv => kv.Key.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) != -1)
                .OrderBy(kv => kv.Key)
                .Skip(startIndex)
                .ToList();

            var results = new List<UISearchMetadata>();
            
            // UIMetadataResource may not be available
            // Given that this is the 'Installed' filter, we ignore failures in reaching the remote server
            // Instead, we will use the local UIMetadataResource
            UIMetadataResource metadataResource;
            try
            {
                metadataResource =
                _sourceRepository == null ?
                null :
                await _sourceRepository.GetResourceAsync<UIMetadataResource>();
            }
            catch (Exception ex)
            {
                metadataResource = null;
                // Write stack to activity log
                Microsoft.VisualStudio.Shell.ActivityLog.LogError(LogEntrySource, ex.ToString());
            }

            var tasks = new List<Task<UISearchMetadata>>();
            foreach (var installedPackage in installedPackages)
            {
                var packageIdentity = new PackageIdentity(
                    installedPackage.Key,
                    installedPackage.Value.Last());
                tasks.Add(
                    Task.Run(() =>
                        GetPackageMetadataAsync(_localResource,
                                                metadataResource,
                                                packageIdentity,
                                                cancellationToken)));
            }

            await Task.WhenAll(tasks);

            foreach (var task in tasks)
            {
                results.Add(task.Result);
            }

            return new SearchResult
            {
                Items = results,
                HasMoreItems = false,
            };
        }

        /// <summary>
        /// Get the metadata of an installed package.
        /// </summary>
        /// <param name="localResource">The local resource, i.e. the package folder of the solution.</param>
        /// <param name="metadataResource">The remote metadata resource.</param>
        /// <param name="identity">The installed package.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The metadata of the package.</returns>
        private async Task<UISearchMetadata> GetPackageMetadataAsync(
            UIMetadataResource localResource,
            UIMetadataResource metadataResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            if (metadataResource == null)
            {
                return await GetPackageMetadataWhenRemoteSourceUnavailable(
                    localResource,
                    identity,
                    cancellationToken);
            }

            try
            {
                var metadata = await GetPackageMetadataFromMetadataResourceAsync(
                    metadataResource,
                    identity,
                    cancellationToken);

                // if the package does not exist in the remote source, NuGet should
                // try getting metadata from the local resource.
                if (String.IsNullOrEmpty(metadata.Summary) && localResource != null)
                {
                    return await GetPackageMetadataWhenRemoteSourceUnavailable(
                        localResource,
                        identity,
                        cancellationToken);
                }
                else
                {
                    return metadata;
                }
            }
            catch
            {
                // When a v2 package source throws, it throws an InvalidOperationException or WebException
                // When a v3 package source throws, it throws an HttpRequestException

                // The remote source is not available. NuGet should not fail but
                // should use the local resource instead.
                if (localResource != null)
                {
                    return await GetPackageMetadataWhenRemoteSourceUnavailable(
                        localResource,
                        identity,
                        cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }

        private async Task<UISearchMetadata> GetPackageMetadataFromMetadataResourceAsync(
            UIMetadataResource metadataResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            var uiPackageMetadatas = await metadataResource.GetMetadata(
                identity.Id,
                _includePrerelease,
                includeUnlisted: false,
                token: cancellationToken);
            var packageMetadata = uiPackageMetadatas.FirstOrDefault(p => p.Identity.Version == identity.Version);

            string summary = string.Empty;
            string title = identity.Id;
            string author = string.Empty;
            if (packageMetadata != null)
            {
                summary = packageMetadata.Summary;
                if (string.IsNullOrEmpty(summary))
                {
                    summary = packageMetadata.Description;
                }
                if (!string.IsNullOrEmpty(packageMetadata.Title))
                {
                    title = packageMetadata.Title;
                }

                author = string.Join(", ", packageMetadata.Authors);
            }

            var versions = uiPackageMetadatas.OrderByDescending(m => m.Identity.Version)
                .Select(m => new VersionInfo(m.Identity.Version, m.DownloadCount));
            return new UISearchMetadata(
                identity,
                title: title,
                summary: summary,
                author: author,
                downloadCount: packageMetadata == null ? null : packageMetadata.DownloadCount,
                iconUrl: packageMetadata == null ? null : packageMetadata.IconUrl,
                versions: ToLazyTask(versions),
                latestPackageMetadata: packageMetadata);
        }

        // Gets the package metadata from the local resource when the remote source
        // is not available.
        private static async Task<UISearchMetadata> GetPackageMetadataWhenRemoteSourceUnavailable(
            UIMetadataResource localResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            UIPackageMetadata packageMetadata = null;
            if (localResource != null)
            {
                var localMetadata = await localResource.GetMetadata(
                    identity.Id,
                    includePrerelease: true,
                    includeUnlisted: true,
                    token: cancellationToken);
                packageMetadata = localMetadata.FirstOrDefault(p => p.Identity.Version == identity.Version);
            }

            string summary = string.Empty;
            string title = identity.Id;
            string author = string.Empty;
            if (packageMetadata != null)
            {
                summary = packageMetadata.Summary;
                if (string.IsNullOrEmpty(summary))
                {
                    summary = packageMetadata.Description;
                }
                if (!string.IsNullOrEmpty(packageMetadata.Title))
                {
                    title = packageMetadata.Title;
                }
                author = string.Join(", ", packageMetadata.Authors);
            }

            var versions = new List<VersionInfo>
            {
                new VersionInfo(identity.Version, downloadCount: null)
            };

            return new UISearchMetadata(
                identity,
                title: title,
                summary: summary,
                author: author,
                downloadCount: packageMetadata.DownloadCount,
                iconUrl: packageMetadata == null ? null : packageMetadata.IconUrl,
                versions: ToLazyTask(versions),
                latestPackageMetadata: null);
        }



        private static Lazy<Task<IEnumerable<VersionInfo>>> ToLazyTask(IEnumerable<VersionInfo> versions)
        {
            return new Lazy<Task<IEnumerable<VersionInfo>>>(() => Task.FromResult(versions));
        }

        public async Task<LoadResult> LoadItemsAsync(int startIndex, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadBegin);

            List<SearchResultPackageMetadata> packages = new List<SearchResultPackageMetadata>();

            var results = await SearchInstalledAsync(startIndex, cancellationToken);

            int resultCount = 0;

            foreach (var package in results.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resultCount++;

                var searchResultPackage = new SearchResultPackageMetadata();
                searchResultPackage.Id = package.Identity.Id;
                searchResultPackage.Version = package.Identity.Version;
                searchResultPackage.IconUrl = package.IconUrl;
                searchResultPackage.Author = package.Author;
                searchResultPackage.DownloadCount = package.DownloadCount;

                if (_installedPackages.ContainsKey(searchResultPackage.Id))
                {
                    var installedVersions = _installedPackages[searchResultPackage.Id];
                    if (installedVersions.Count > 1)
                    {
                        //!!! what should we do here?
                    }
                    else
                    {
                        searchResultPackage.InstalledVersion = installedVersions.Last();
                    }
                }

                var versionList = new Lazy<Task<IEnumerable<VersionInfo>>>(async () =>
                {
                    var versions = await package.Versions.Value;

                    var filteredVersions = versions
                            .Where(v => !v.Version.IsPrerelease || _includePrerelease)
                            .ToList();

                    if (!filteredVersions.Any(v => v.Version == searchResultPackage.Version))
                    {
                        filteredVersions.Add(new VersionInfo(searchResultPackage.Version, downloadCount: null));
                    }

                    return filteredVersions;
                });

                searchResultPackage.Versions = versionList;

                searchResultPackage.BackgroundLoader = new Lazy<Task<BackgroundLoaderResult>>(
                    () => BackgroundLoad(searchResultPackage.Id, versionList));

                // filter out prerelease version when needed.
                if (searchResultPackage.Version.IsPrerelease &&
                    !_includePrerelease)
                {
                    var value = await searchResultPackage.BackgroundLoader.Value;

                    if (value.Status == PackageStatus.NotInstalled)
                    {
                        continue;
                    }
                }

                searchResultPackage.Summary = package.Summary;
                packages.Add(searchResultPackage);
            }

            cancellationToken.ThrowIfCancellationRequested();
            NuGetEventTrigger.Instance.TriggerEvent(NuGetEvent.PackageLoadEnd);
            return new LoadResult()
            {
                Items = packages,
                HasMoreItems = results.HasMoreItems,
                NextStartIndex = startIndex + resultCount
            };
        }

        // Load info in the background
        private async Task<BackgroundLoaderResult> BackgroundLoad(string id, Lazy<Task<IEnumerable<VersionInfo>>> versions)
        {
            if (_installedPackages.ContainsKey(id))
            {
                var versionsUnwrapped = await versions.Value;
                var highestAvailableVersion = versionsUnwrapped
                    .Select(v => v.Version)
                    .Max();
                var lowestInstalledVersion = _installedPackages[id].First();

                if (VersionComparer.VersionRelease.Compare(lowestInstalledVersion, highestAvailableVersion) < 0)
                {
                    return new BackgroundLoaderResult()
                    {
                        LatestVersion = highestAvailableVersion,
                        Status = PackageStatus.UpdateAvailable
                    };
                }

                return new BackgroundLoaderResult()
                {
                    Status = PackageStatus.Installed
                };
            }

            return new BackgroundLoaderResult()
            {
                Status = PackageStatus.NotInstalled
            };
        }
    }
}
