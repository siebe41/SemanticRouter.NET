using Microsoft.Data.Sqlite;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.Support;

/// <summary>Lets go of the SQLite files a test made.</summary>
internal static class SqliteFiles
{
    /// <summary>
    /// Closes the pooled connections, the router's and the test's own, to the databases under
    /// <paramref name="directory"/>, so it can be deleted (Windows refuses to delete an open file). Not
    /// <see cref="SqliteConnection.ClearAllPools"/>: that closes other tests' connections too, and closing the last
    /// connection to a database deletes its -wal file under a test that is reading it.
    /// </summary>
    public static void Release(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            SqliteRouterDatabase.ClearPool(file);
            using var own = new SqliteConnection($"Data Source={file}");
            SqliteConnection.ClearPool(own);
        }
    }
}
