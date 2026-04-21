// Stub types placed in the System.Data.SqlClient namespace so that ClrMD reports
// them as "System.Data.SqlClient.SqlConnection" — exactly what DumpDetective
// connection-pool looks for in a heap walk.
namespace System.Data.SqlClient;

internal sealed class SqlConnection
{
    public string ConnectionString;
    public int    State;           // 0 = Closed, 1 = Open, 2 = Connecting, 4 = Executing, 8 = Fetching
    public string DataSource;
    public string Database;

    public SqlConnection(string connectionString, string dataSource, string database, int state)
    {
        ConnectionString = connectionString;
        DataSource       = dataSource;
        Database         = database;
        State            = state;
    }
}
