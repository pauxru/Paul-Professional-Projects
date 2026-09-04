package dev.migver;

import java.math.BigDecimal;
import java.sql.*;
import java.util.*;

/**
 * One side of the migration, behind a deliberately thin interface.
 *
 * <p>The interface is thin because the whole point is that the two sides are
 * <em>not</em> interchangeable. Anything that papers over a difference here --
 * a helpful type coercion, a normalising getter -- is a difference the verifier
 * can no longer see, and the differences are what we are looking for. So this
 * class does as little as possible: it opens a connection, runs DDL, writes rows
 * through {@link PreparedStatement}, and reads them back as whatever the driver
 * chooses to hand over.
 *
 * <p>In particular {@link #read} calls {@code getObject}, not {@code getString}.
 * A driver's {@code getString} is a rendering, and rendering differences are
 * exactly the false positives that make a naive verifier useless.
 */
public final class Engine implements AutoCloseable {

    public enum Kind {
        /** Stands in for the source: strict types, enforced scale, real collations. */
        H2("jdbc:h2:mem:%s;DB_CLOSE_DELAY=-1", "org.h2.Driver"),
        /** Stands in for the target: type affinity, advisory scale, ASCII-only NOCASE. */
        SQLITE("jdbc:sqlite:file:%s?mode=memory&cache=shared", "org.sqlite.JDBC");

        private final String urlTemplate;
        private final String driver;

        Kind(String urlTemplate, String driver) {
            this.urlTemplate = urlTemplate;
            this.driver = driver;
        }
    }

    private final Kind kind;
    private final Connection connection;

    private Engine(Kind kind, Connection connection) {
        this.kind = kind;
        this.connection = connection;
    }

    public static Engine open(Kind kind, String name) {
        try {
            Class.forName(kind.driver);
            return new Engine(kind, DriverManager.getConnection(String.format(kind.urlTemplate, name)));
        } catch (ClassNotFoundException | SQLException e) {
            throw new IllegalStateException("could not open " + kind + " '" + name + "'", e);
        }
    }

    public Kind kind() {
        return kind;
    }

    public void execute(String sql) {
        try (Statement s = connection.createStatement()) {
            s.execute(sql);
        } catch (SQLException e) {
            throw new IllegalStateException(kind + ": " + sql, e);
        }
    }

    /**
     * DDL is per-engine because the type names and collation syntax differ; that
     * is the point.
     *
     * <p>Both sides are configured to be case-insensitive on {@code name}, which
     * is what the migration ticket says: "target collation NOCASE, equivalent to
     * source". The two mechanisms are not equivalent, and
     * {@link Hazard#COLLATION_FOLDS_LESS} is what the difference costs.
     */
    public void createTable(String table) {
        String collate = "";
        // The account column is the schema modernisation itself: the legacy
        // source holds account numbers as text, because that is what a 1990s
        // schema does, and the new target tightens them to INTEGER, because that
        // is what a reviewer asks for. Both decisions are defensible. Together
        // they are AFFINITY_COERCION, and the row where it matters is the one
        // customer whose account number begins with zeros.
        String account = "account VARCHAR(24)";
        if (kind == Kind.SQLITE) {
            collate = " COLLATE NOCASE";
            account = "account INTEGER";
        } else {
            // Must precede table creation; H2 binds the setting at DDL time.
            execute("SET IGNORECASE TRUE");
        }
        execute("DROP TABLE IF EXISTS " + table);
        execute("CREATE TABLE " + table + " ("
                + "id INTEGER PRIMARY KEY, "
                + "name VARCHAR(60)" + collate + ", "
                + "code CHAR(10), "
                + account + ", "
                + "amount DECIMAL(18,4), "
                + "active BOOLEAN, "
                + "seen TIMESTAMP)");
    }

    public void insert(String table, Row row) {
        String sql = "INSERT INTO " + table + " (id, name, code, account, amount, active, seen) VALUES (?,?,?,?,?,?,?)";
        try (PreparedStatement ps = connection.prepareStatement(sql)) {
            ps.setLong(1, row.id());
            setOrNull(ps, 2, row.name(), Types.VARCHAR);
            setOrNull(ps, 3, row.code(), Types.CHAR);

            // Bound as a *string* on purpose. Account numbers arrive from the
            // source as text -- that is the whole hazard -- and binding them as a
            // long here would silently fix the bug we are trying to observe.
            setOrNull(ps, 4, row.account(), Types.VARCHAR);

            if (row.amount() == null) {
                ps.setNull(5, Types.DECIMAL);
            } else {
                ps.setBigDecimal(5, row.amount());
            }
            if (row.active() == null) {
                ps.setNull(6, Types.BOOLEAN);
            } else {
                ps.setBoolean(6, row.active());
            }
            setOrNull(ps, 7, row.seen(), Types.VARCHAR);
            ps.execute();
        } catch (SQLException e) {
            throw new IllegalStateException(kind + " insert id=" + row.id(), e);
        }
    }

    private static void setOrNull(PreparedStatement ps, int i, String v, int type) throws SQLException {
        if (v == null) {
            ps.setNull(i, type);
        } else {
            ps.setString(i, v);
        }
    }

    /**
     * Write a row that came out of {@link #read} and possibly through a
     * {@link Migrator}, binding each value as whatever type it now is.
     *
     * <p>{@code setObject} rather than a typed setter, deliberately: if a
     * migrator turned an account string into a {@code Long}, the write must
     * behave the way it would in the real pipeline, where nothing intervenes to
     * turn it back.
     */
    public void insertMap(String table, Map<String, Object> row) {
        String sql = "INSERT INTO " + table
                + " (id, name, code, account, amount, active, seen) VALUES (?,?,?,?,?,?,?)";
        String[] cols = {"id", "name", "code", "account", "amount", "active", "seen"};
        try (PreparedStatement ps = connection.prepareStatement(sql)) {
            for (int i = 0; i < cols.length; i++) {
                Object v = row.get(cols[i]);
                if (v == null) {
                    ps.setNull(i + 1, Types.OTHER);
                } else {
                    ps.setObject(i + 1, v);
                }
            }
            ps.execute();
        } catch (SQLException e) {
            throw new IllegalStateException(kind + " insertMap id=" + row.get("id"), e);
        }
    }

    /**
     * Read every row back as the driver chose to represent it.
     *
     * <p>The {@code Object} values are heterogeneous by design: a DECIMAL comes
     * back as {@link BigDecimal} from H2 and {@link Double} from SQLite, and
     * flattening that here would hide {@link Hazard#DECIMAL_TO_BINARY_FLOAT}
     * from every comparison strategy downstream.
     */
    public List<Map<String, Object>> read(String table) {
        List<Map<String, Object>> rows = new ArrayList<>();
        try (ResultSet rs = connection.createStatement().executeQuery("SELECT * FROM " + table + " ORDER BY id")) {
            ResultSetMetaData md = rs.getMetaData();
            while (rs.next()) {
                Map<String, Object> row = new LinkedHashMap<>();
                for (int i = 1; i <= md.getColumnCount(); i++) {
                    row.put(md.getColumnName(i).toLowerCase(Locale.ROOT), rs.getObject(i));
                }
                rows.add(row);
            }
        } catch (SQLException e) {
            throw new IllegalStateException(kind + " read " + table, e);
        }
        return rows;
    }

    public long count(String table) {
        try (ResultSet rs = connection.createStatement().executeQuery("SELECT COUNT(*) FROM " + table)) {
            rs.next();
            return rs.getLong(1);
        } catch (SQLException e) {
            throw new IllegalStateException(kind + " count " + table, e);
        }
    }

    /**
     * Rows the engine considers duplicates of one another under the column's own
     * collation. This is how {@link Hazard#COLLATION_FOLDS_LESS} becomes visible:
     * the two engines disagree about how many distinct names they are holding,
     * while agreeing exactly on how many rows they are holding.
     */
    public long distinctNames(String table) {
        try (ResultSet rs = connection.createStatement()
                .executeQuery("SELECT COUNT(DISTINCT name) FROM " + table)) {
            rs.next();
            return rs.getLong(1);
        } catch (SQLException e) {
            throw new IllegalStateException(kind + " distinct " + table, e);
        }
    }

    @Override
    public void close() {
        try {
            connection.close();
        } catch (SQLException ignored) {
            // Nothing useful to do; the process is about to end either way.
        }
    }
}
