using System.Collections.Concurrent;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace PackageDownloadsExample
{
    /// <summary>
    ///     Find the download count for each version of a package.
    /// </summary>
    public class Program
    {
        private const string DownloadDirectory = "/nuget-downloads/";
        private static readonly ConcurrentBag<string> Handled = new ConcurrentBag<string>();
        private static readonly SourceCacheContext SourceCacheContext = new SourceCacheContext();
        private static ConsoleColor _defaultColor;
        private static readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        /// <summary>
        ///     Start
        /// </summary>
        /// <param name="packageId"></param>
        /// <param name="exactMatch"></param>
        public static async Task Main(string packageId, bool exactMatch = false)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                Console.WriteLine(
                    "The --package-id option is required. Run --help for more information");

                return;
            }

            _defaultColor = Console.ForegroundColor;

            if (!Directory.Exists(DownloadDirectory))
                Directory.CreateDirectory(DownloadDirectory);


            // https://www.meziantou.net/exploring-the-nuget-client-libraries.htm

            var repository = GetNugetRepository();

            await HandlePackage(packageId, repository, 0, exactMatch, null);

            Console.ForegroundColor = _defaultColor;

            Console.WriteLine($"{Handled.Count} packages handled!");
        }

        private static SourceRepository GetNugetRepository()
        {
            var source = new PackageSource("https://api.nuget.org/v3/index.json");
            var providers = Repository.Provider.GetCoreV3();
            var repository = new SourceRepository(source, providers);
            return repository;
        }

        private static async Task HandlePackage(string packageId, SourceRepository repository,
            int level, bool exactMatch, string version)
        {
	        if (!string.IsNullOrEmpty(version))
	        {
		        var packageAndVersion = packageId + "." + version;
		        if (Handled.Contains(packageAndVersion))
			        return;

		        Handled.Add(packageAndVersion);
            }

            var search = await repository.GetResourceAsync<RawSearchResourceV3>();

            var filter = new SearchFilter(false, SearchFilterType.IsLatestVersion)
            {
                SupportedFrameworks = new List<string> { "net48", "net5.0", "net6.0" }
            };

            var response = await search.Search(packageId, filter, 0, 1000, NullLogger.Instance,
                CancellationToken.None);

            var results = response.Select(result => result.ToObject<SearchResult>()).ToList();

            // https://docs.microsoft.com/en-us/nuget/reference/nuget-client-sdk

            results = exactMatch
                ? results.Where(r => r.PackageId.ToLowerInvariant() == packageId.ToLowerInvariant())
                    .ToList()
                : results.Where(r =>
                        r.PackageId.ToLowerInvariant().StartsWith(packageId.ToLowerInvariant()))
                    .ToList();

            await Parallel.ForEachAsync(results, new ParallelOptions { MaxDegreeOfParallelism = 1 },
                async (result, token) =>
                {
                    var tabs = "";
                    for (var i = 0; i < level; i++)
                        tabs += "  ";

                    //TODO: Manage minimum required version

                    //dependencies
                    var dependencies = await GetPackageDependencies(repository, result,
                        SourceCacheContext, new CancellationToken());

                    dependencies.RemoveAll(d =>
                        !d.TargetFramework.Framework.ToLowerInvariant().StartsWith(".net"));

                    var packages = dependencies.SelectMany(d => d.Packages).ToList();
                    packages.RemoveAll(p => Handled.Contains(p.Id + "." + p.VersionRange.MinVersion.ToNormalizedString()));
                    packages = packages.DistinctBy(p => new { p.Id, p.VersionRange.MinVersion }).ToList();

                    if (packages.Any())
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine(
                            $"{tabs}{packages.Count} unhandled dependencies for {result.PackageId}");

                        //foreach (var package in packages)
                        await Parallel.ForEachAsync(packages,
                            new ParallelOptions { MaxDegreeOfParallelism = 1 },
                            async (package, token) =>
                            {
                                var version = package.VersionRange.MinVersion.ToNormalizedString();
                                await HandlePackage(package.Id, repository, level + 1, true, version);
                            });
                    }

                    var packageWithVersion = $"{result.PackageId}.{result.Versions.Last().Version}";
                    var filePath = Path.Combine(DownloadDirectory, packageWithVersion + ".nupkg");

                    await semaphoreSlim.WaitAsync();
                    try
                    {
                        if (!File.Exists(filePath))
                        {
                            try
                            {
                                await DownloadPackageFile(filePath, repository, result);

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"{tabs}{packageWithVersion} downloaded!");


                            }
                            catch (Exception e)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"Error: {e.Message}");
                            }
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine($"{tabs}{packageWithVersion} already downloaded.");
                        }
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }
                });
        }

        private static async Task DownloadPackageFile(string downloadPath,
            SourceRepository repository, SearchResult result)
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            await using var fileStream = File.OpenWrite(downloadPath);
            await resource.CopyNupkgToStreamAsync(result.PackageId,
                new NuGetVersion(result.Versions.Last().Version), fileStream, SourceCacheContext,
                NullLogger.Instance, CancellationToken.None);
        }

        private static async Task<List<PackageDependencyGroup>> GetPackageDependencies(
            SourceRepository repository, SearchResult result, SourceCacheContext cache,
            CancellationToken cancellationToken)
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            await using var packageStream = new MemoryStream();
            await resource.CopyNupkgToStreamAsync(result.PackageId,
                new NuGetVersion(result.Versions.Last().Version), packageStream, cache,
                NullLogger.Instance, CancellationToken.None);

            using var packageReader = new PackageArchiveReader(packageStream);
            var nuspecReader = await packageReader.GetNuspecReaderAsync(cancellationToken);
            var dependencies = nuspecReader.GetDependencyGroups();
            return dependencies.ToList();
        }
    }
}