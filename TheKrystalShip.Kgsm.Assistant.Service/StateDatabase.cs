using Microsoft.Data.Sqlite;

using TheKrystalShip.Llm.Conversation;

namespace TheKrystalShip.Kgsm.Assistant.Service;

/// <summary>
/// Where this service's durable state lives: one SQLite file, shared by every store that needs one.
/// </summary>
/// <remarks>
/// The conversation history picked the path and everything else follows it — one state file to
/// configure, back up and hand to <c>StateDirectory=</c>, rather than a path per concern. Each store
/// still owns its own tables and creates them idempotently, so none of them assumes anything about
/// which ran first.
/// </remarks>
internal static class StateDatabase
{
    /// <summary>
    /// The connection string for the shared state file, creating its directory if it is not there yet.
    /// </summary>
    public static string ConnectionString(ConversationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A configured path wins; otherwise a file beside the host binary, which is what a bare
        // `dotnet run` gets and what the conversation store has always defaulted to.
        var databasePath = string.IsNullOrWhiteSpace(options.DatabasePath)
            ? Path.Combine(AppContext.BaseDirectory, "conversations.db")
            : options.DatabasePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary>An open connection to the shared state file.</summary>
    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
