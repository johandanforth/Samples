using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
		private static readonly List<string> Handled = new List<string>();
		private static readonly SourceCacheContext SourceCacheContext = new SourceCacheContext();
		private static ConsoleColor _defaultColor;

		/// <summary>
		///     Find the download count for each version of a package.
		/// </summary>
		/// <param name="packageId">The package ID. Example: "newtonsoft.json"</param>
		public static async Task Main(string packageId, bool wide = false)
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

			var source = new PackageSource("https://api.nuget.org/v3/index.json");
			var providers = Repository.Provider.GetCoreV3();
			var repository = new SourceRepository(source, providers);

			await HandlePackage(packageId, repository, 0, wide);

			Console.ForegroundColor = _defaultColor;
		}

		private static async Task HandlePackage(string packageId, SourceRepository repository, int level,
			bool isFirst = false, string version = null)
		{
			var search = await repository.GetResourceAsync<RawSearchResourceV3>();

			var filter = new SearchFilter(false, SearchFilterType.IsLatestVersion)
			{
				SupportedFrameworks = new List<string>
				{
					"net48", 
					"net5.0",
					//"net6.0"
				}
			};

			//	var query = isFirst ? $"id:\"{packageId}\"" : $"package-id:\"{packageId}\"";

			var response = await search.Search(packageId, filter, 0, 200, NullLogger.Instance,
				CancellationToken.None);

			var results = response.Select(result => result.ToObject<SearchResult>()).ToList();

			// https://docs.microsoft.com/en-us/nuget/reference/nuget-client-sdk

			results = isFirst
				? results.Where(r =>
						r.PackageId.ToLowerInvariant().StartsWith(packageId.ToLowerInvariant()))
					.ToList()
				: results.Where(r => r.PackageId == packageId).ToList();

			foreach (var result in results)
			{
				for (int i = 0; i < level; i++)
					Console.Write("\t");

				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write($" {result.PackageId} {result.Versions.Last().Version} ");

				var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

				var packageName = $"{result.PackageId}.{result.Versions.Last().Version}";
				var filePath = Path.Combine(DownloadDirectory, packageName + ".nupkg");

				if (!File.Exists(filePath))
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.Write("downloading... ");
					await DownloadPackageFile(filePath, resource, result);
					Console.ForegroundColor = ConsoleColor.Green;
					Console.WriteLine("done!");

					if (!Handled.Contains(result.PackageId))
						Handled.Add(result.PackageId);

					//dependencies
					var deps = await GetDependencies(resource, result, SourceCacheContext,
						new CancellationToken());

					var packages = deps
						.Where(d =>
							d.TargetFramework.Framework.ToLowerInvariant().StartsWith(".net"))
						.SelectMany(d => d.Packages).Distinct().
						Where(p => !Handled.Contains(p.Id));

					foreach (var package in packages)
						if (!Handled.Contains(package.Id))
						{
							await HandlePackage(package.Id, repository, level+1);
						}
				}
				else
				{
					Console.ForegroundColor = ConsoleColor.DarkGray;
					Console.WriteLine("already downloaded.");
				}

			}
		}

		private static async Task DownloadPackageFile(string downloadPath,
			FindPackageByIdResource resource, SearchResult result)
		{
			await using var fileStream = File.OpenWrite(downloadPath);
			await resource.CopyNupkgToStreamAsync(result.PackageId,
				new NuGetVersion(result.Versions.Last().Version), fileStream, SourceCacheContext,
				NullLogger.Instance, CancellationToken.None);
		}

		private static async Task<IEnumerable<PackageDependencyGroup>> GetDependencies(
			FindPackageByIdResource resource, SearchResult result, SourceCacheContext cache,
			CancellationToken cancellationToken)
		{
			await using var packageStream = new MemoryStream();
			await resource.CopyNupkgToStreamAsync(result.PackageId,
				new NuGetVersion(result.Versions.Last().Version), packageStream, cache,
				NullLogger.Instance, CancellationToken.None);

			using var packageReader = new PackageArchiveReader(packageStream);
			var nuspecReader = await packageReader.GetNuspecReaderAsync(cancellationToken);
			var deps = nuspecReader.GetDependencyGroups();
			return deps;
		}
	}
}