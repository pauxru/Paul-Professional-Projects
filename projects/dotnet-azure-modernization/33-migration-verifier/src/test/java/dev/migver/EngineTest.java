package dev.migver;

import org.junit.jupiter.api.*;

import java.math.BigDecimal;
import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/**
 * What the two engines actually do, asserted directly against the drivers.
 *
 * <p>These are the measurements the whole taxonomy rests on. If a driver version
 * changes behaviour, these fail first and loudest, before any of the derived
 * claims in the report have a chance to become quietly wrong.
 */
class EngineTest {

    @BeforeAll
    static void pinZone() {
        TimeZone.setDefault(TimeZone.getTimeZone("UTC"));
    }

    private static Engine open(Engine.Kind k, String name) {
        Engine e = Engine.open(k, name);
        e.createTable("t");
        return e;
    }

    @Test
    void sourceIsCaseInsensitiveAcrossTheWholeAlphabet() {
        try (Engine e = open(Engine.Kind.H2, "eng1")) {
            e.insert("t", Row.benign(1, "\u00c4pfel"));
            e.insert("t", Row.benign(2, "\u00e4pfel"));
            assertEquals(1, e.distinctNames("t"),
                    "H2 with IGNORECASE folds non-ASCII case as well as ASCII");
        }
    }

    @Test
    void targetFoldsAsciiOnly() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng2")) {
            e.insert("t", Row.benign(1, "\u00c4pfel"));
            e.insert("t", Row.benign(2, "\u00e4pfel"));
            assertEquals(2, e.distinctNames("t"),
                    "SQLite NOCASE folds A-Z only, so the German pair stays distinct");
        }
    }

    @Test
    void bothFoldAscii() {
        try (Engine s = open(Engine.Kind.H2, "eng3"); Engine t = open(Engine.Kind.SQLITE, "eng3b")) {
            for (Engine e : List.of(s, t)) {
                e.insert("t", Row.benign(1, "ACME LTD"));
                e.insert("t", Row.benign(2, "acme ltd"));
                assertEquals(1, e.distinctNames("t"), e.kind() + " folds ASCII case");
            }
        }
    }

    @Test
    void theCollationDifferenceIsInvisibleOnAsciiData() {
        try (Engine s = open(Engine.Kind.H2, "eng4"); Engine t = open(Engine.Kind.SQLITE, "eng4b")) {
            for (Engine e : List.of(s, t)) {
                e.insert("t", Row.benign(1, "Smith"));
                e.insert("t", Row.benign(2, "SMITH"));
            }
            assertEquals(s.distinctNames("t"), t.distinctNames("t"),
                    "an English-only test corpus cannot detect this hazard at all");
        }
    }

    @Test
    void sourceKeepsDecimalAsBigDecimal() {
        try (Engine e = open(Engine.Kind.H2, "eng5")) {
            e.insert("t", Row.benign(1, "x"));
            assertInstanceOf(BigDecimal.class, e.read("t").get(0).get("amount"));
        }
    }

    @Test
    void targetDoesNotKeepDecimalAsBigDecimal() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng6")) {
            e.insert("t", Row.benign(1, "x"));
            Object amount = e.read("t").get(0).get("amount");
            assertFalse(amount instanceof BigDecimal,
                    "SQLite has no decimal type; got " + amount.getClass().getSimpleName());
        }
    }

    @Test
    void sourceEnforcesDeclaredScale() {
        try (Engine e = open(Engine.Kind.H2, "eng7")) {
            e.insert("t", new Row(1, "x", "C", "1", new BigDecimal("2.34567"), true,
                    "2024-01-01 00:00:00", null, false));
            BigDecimal got = (BigDecimal) e.read("t").get(0).get("amount");
            assertEquals(4, got.scale(), "DECIMAL(18,4) rounds to four places on write");
        }
    }

    @Test
    void sourceRejectsAnIntegerTooWideForItsColumn() {
        try (Engine e = Engine.open(Engine.Kind.H2, "eng8")) {
            e.execute("CREATE TABLE w (id INTEGER PRIMARY KEY, v INTEGER)");
            assertThrows(IllegalStateException.class,
                    () -> e.execute("INSERT INTO w VALUES (1, 9223372036854775807)"),
                    "H2's INTEGER is 32-bit and says so; a hazard that throws is a bug report");
        }
    }

    @Test
    void targetsIntegerIsWider() {
        try (Engine e = Engine.open(Engine.Kind.SQLITE, "eng9")) {
            e.execute("CREATE TABLE w (id INTEGER PRIMARY KEY, v INTEGER)");
            e.execute("INSERT INTO w VALUES (1, 9223372036854775807)");
            assertEquals(1, e.count("w"), "the same type name means a different width here");
        }
    }

    @Test
    void targetCoercesLeadingZerosAwayOnAnIntegerColumn() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng10")) {
            e.insert("t", new Row(1, "x", "C", "0000007", BigDecimal.ONE, true,
                    "2024-01-01 00:00:00", null, false));
            assertEquals("7", String.valueOf(e.read("t").get(0).get("account")));
        }
    }

    @Test
    void sourceKeepsLeadingZerosOnItsTextColumn() {
        try (Engine e = open(Engine.Kind.H2, "eng11")) {
            e.insert("t", new Row(1, "x", "C", "0000007", BigDecimal.ONE, true,
                    "2024-01-01 00:00:00", null, false));
            assertEquals("0000007", e.read("t").get(0).get("account"),
                    "the corruption is introduced by the modernised schema, not by the legacy one");
        }
    }

    @Test
    void sourcePadsFixedWidthColumns() {
        try (Engine e = open(Engine.Kind.H2, "eng12")) {
            e.insert("t", Row.benign(1, "x"));
            assertEquals(10, ((String) e.read("t").get(0).get("code")).length());
        }
    }

    @Test
    void targetDoesNotPadFixedWidthColumns() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng13")) {
            e.insert("t", Row.benign(1, "x"));
            assertEquals(2, ((String) e.read("t").get(0).get("code")).length());
        }
    }

    @Test
    void sourceReturnsBooleansAsBooleans() {
        try (Engine e = open(Engine.Kind.H2, "eng14")) {
            e.insert("t", Row.benign(1, "x"));
            assertInstanceOf(Boolean.class, e.read("t").get(0).get("active"));
        }
    }

    @Test
    void targetReturnsBooleansAsIntegers() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng15")) {
            e.insert("t", Row.benign(1, "x"));
            assertInstanceOf(Integer.class, e.read("t").get(0).get("active"));
        }
    }

    @Test
    void bothEnginesDistinguishEmptyStringFromNull() {
        try (Engine s = open(Engine.Kind.H2, "eng16"); Engine t = open(Engine.Kind.SQLITE, "eng16b")) {
            for (Engine e : List.of(s, t)) {
                e.insert("t", new Row(1, "", "C", "1", BigDecimal.ONE, true, "2024-01-01 00:00:00", null, false));
                e.insert("t", new Row(2, null, "C", "2", BigDecimal.ONE, true, "2024-01-01 00:00:00", null, false));
                List<Map<String, Object>> rows = e.read("t");
                assertEquals("", rows.get(0).get("name"), e.kind() + " keeps the empty string");
                assertNull(rows.get(1).get("name"), e.kind() + " keeps NULL");
            }
        }
    }

    @Test
    void bothEnginesPreserveTrailingSpaceInVariableWidthText() {
        try (Engine s = open(Engine.Kind.H2, "eng17"); Engine t = open(Engine.Kind.SQLITE, "eng17b")) {
            for (Engine e : List.of(s, t)) {
                e.insert("t", Row.benign(1, "Smith "));
                assertEquals("Smith ", e.read("t").get(0).get("name"), e.kind().toString());
            }
        }
    }

    @Test
    void bothEnginesKeepNfcAndNfdApart() {
        try (Engine s = open(Engine.Kind.H2, "eng18"); Engine t = open(Engine.Kind.SQLITE, "eng18b")) {
            for (Engine e : List.of(s, t)) {
                e.insert("t", Row.benign(1, "caf\u00e9"));
                e.insert("t", Row.benign(2, "cafe\u0301"));
                assertEquals(2, e.distinctNames("t"), e.kind() + " sees two names, a human sees one");
            }
        }
    }

    @Test
    void targetAcceptsATimeThatDoesNotExistInSomeZones() {
        try (Engine e = open(Engine.Kind.SQLITE, "eng19")) {
            e.insert("t", new Row(1, "x", "C", "1", BigDecimal.ONE, true,
                    "2024-03-31 02:30:00", null, false));
            assertEquals(1, e.count("t"), "no date type means no opinion about impossible dates");
        }
    }

    @Test
    void readReturnsDriverNativeTypesRatherThanRenderings() {
        try (Engine e = open(Engine.Kind.H2, "eng20")) {
            e.insert("t", Row.benign(1, "x"));
            Map<String, Object> row = e.read("t").get(0);
            assertFalse(row.get("active") instanceof String,
                    "getString would flatten every type divergence this project exists to observe");
            assertFalse(row.get("amount") instanceof String);
        }
    }

    @Test
    void twoEnginesWithDifferentNamesDoNotShareState() {
        try (Engine a = open(Engine.Kind.SQLITE, "isoA"); Engine b = open(Engine.Kind.SQLITE, "isoB")) {
            a.insert("t", Row.benign(1, "only-in-a"));
            assertEquals(1, a.count("t"));
            assertEquals(0, b.count("t"), "shared-cache in-memory databases must still be per-name");
        }
    }

    @Test
    void createTableIsIdempotent() {
        try (Engine e = open(Engine.Kind.H2, "eng21")) {
            e.insert("t", Row.benign(1, "x"));
            e.createTable("t");
            assertEquals(0, e.count("t"), "re-creating drops, so each experiment starts clean");
        }
    }

    @Test
    void insertFailureNamesTheRow() {
        try (Engine e = open(Engine.Kind.H2, "eng22")) {
            e.insert("t", Row.benign(1, "x"));
            IllegalStateException ex = assertThrows(IllegalStateException.class,
                    () -> e.insert("t", Row.benign(1, "duplicate")));
            assertTrue(ex.getMessage().contains("id=1"), ex.getMessage());
        }
    }
}
