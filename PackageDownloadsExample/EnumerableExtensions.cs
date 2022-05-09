namespace PackageDownloadsExample
{
	/// <summary>
	/// </summary>
	public static class EnumerableExtensions
	{
		/// <summary>
		/// </summary>
		/// <param name="source"></param>
		/// <param name="asyncAction"></param>
		/// <param name="maxDegreeOfParallelism"></param>
		/// <param name="cancellationToken"></param>
		/// <typeparam name="T"></typeparam>
		public static async Task ParallelForEachAsync<T>(this IEnumerable<T> source,
			Func<T, CancellationToken, Task> asyncAction, int maxDegreeOfParallelism,
			CancellationToken cancellationToken)
		{
			var throttler = new SemaphoreSlim(maxDegreeOfParallelism);
			var tasks = source.Select(async item =>
			{
				await throttler.WaitAsync(cancellationToken);
				if (cancellationToken.IsCancellationRequested)
					return;

				try
				{
					await asyncAction(item, cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					throttler.Release();
				}
			});

			await Task.WhenAll(tasks);
		}
	}
}