package dev.migver;

import org.junit.jupiter.api.*;

import java.math.BigDecimal;
import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/** The rules, their scoping, and the injectivity criterion. */
class RuleTest {

    @Test
    void trimGovernsTextColumnsOnly() {
        assertTrue(Rule.TRIM.governs("name"));
        assertTrue(Rule.TRIM.governs("code"));
        assertFalse(Rule.TRIM.governs("amount"));
    }

    @Test
    void everyRuleGovernsAtLeastOneColumn() {
        for (Rule r : Rule.values()) {
            assertFalse(r.columns().isEmpty(), r + " governs nothing and can never fire");
        }
    }

    @Test
    void everyGovernedColumnIsOneThatIsActuallyCompared() {
        Set<String> compared = Set.of("name", "code", "account", "amount", "active", "seen");
        for (Rule r : Rule.values()) {
            for (String c : r.columns()) {
                assertTrue(compared.contains(c), r + " governs " + c + ", which is never compared");
            }
        }
    }

    @Test
    void everyRuleHasAMotivation() {
        for (Rule r : Rule.values()) {
            assertTrue(r.motivation().length() > 20, r + " has no stated reason for existing");
        }
    }

    @Test
    void ruleIdsAreUnique() {
        Set<String> ids = new HashSet<>();
        for (Rule r : Rule.values()) {
            assertTrue(ids.add(r.id()), "duplicate rule id " + r.id());
        }
    }

    @Test
    void trimCollapsesValuesThatDiffer() {
        assertEquals(Rule.TRIM.apply("Smith"), Rule.TRIM.apply("Smith "),
                "which is exactly why it is not injective");
    }

    @Test
    void nfcCollapsesValuesThatDiffer() {
        assertEquals(Rule.NFC.apply("caf\u00e9"), Rule.NFC.apply("cafe\u0301"));
    }

    @Test
    void casefoldCollapsesValuesThatDiffer() {
        assertEquals(Rule.CASEFOLD.apply("Smith"), Rule.CASEFOLD.apply("SMITH"));
    }

    @Test
    void numericReconcilesRepresentationsWithoutCollapsingValues() {
        assertEquals(Rule.NUMERIC.apply(new BigDecimal("10.0000")), Rule.NUMERIC.apply(10),
                "the same amount arriving as BigDecimal and as Integer");
        assertNotEquals(Rule.NUMERIC.apply(new BigDecimal("2.345")),
                Rule.NUMERIC.apply(new BigDecimal("2.35")),
                "different amounts must stay different");
    }

    @Test
    void numericDoesNotRouteThroughDouble() {
        Object a = Rule.NUMERIC.apply(new BigDecimal("99999999999999.1234"));
        Object b = Rule.NUMERIC.apply(new BigDecimal("99999999999999.1235"));
        assertNotEquals(a, b, "canonicalising via double would commit the error it tolerates");
    }

    @Test
    void booleanReconcilesBothSpellings() {
        assertEquals(Rule.BOOLEAN.apply(Boolean.TRUE), Rule.BOOLEAN.apply(1));
        assertEquals(Rule.BOOLEAN.apply(Boolean.FALSE), Rule.BOOLEAN.apply(0));
        assertNotEquals(Rule.BOOLEAN.apply(Boolean.TRUE), Rule.BOOLEAN.apply(0));
    }

    @Test
    void booleanLeavesNonBooleanNumbersAlone() {
        assertEquals(7, Rule.BOOLEAN.apply(7));
    }

    @Test
    void identifierReconcilesTextAndIntegerWithoutLosingLeadingZeros() {
        assertEquals(Rule.IDENTIFIER.apply("10001"), Rule.IDENTIFIER.apply(10001));
        assertNotEquals(Rule.IDENTIFIER.apply("0000007"), Rule.IDENTIFIER.apply(7),
                "the entire point: a rule that parsed both as numbers would hide this");
    }

    @Test
    void temporalReconcilesTimestampWithEpochMillis() {
        java.sql.Timestamp t = java.sql.Timestamp.valueOf("2024-01-15 09:00:00");
        assertEquals(Rule.TEMPORAL.apply(t), Rule.TEMPORAL.apply(t.getTime()));
    }

    @Test
    void temporalLeavesSmallIntegersAlone() {
        assertEquals(42L, Rule.TEMPORAL.apply(42L),
                "an account number must not be reinterpreted as a date");
    }

    @Test
    void everyRuleIsNullSafe() {
        for (Rule r : Rule.values()) {
            assertNull(r.apply(null), r + " must pass null through");
        }
    }

    @Test
    void injectiveRulesNeverCollapseTwoDistinctInputs() {
        List<Object> probes = List.of("Smith", "Smith ", "caf\u00e9", "cafe\u0301", "0000007", "7",
                new BigDecimal("2.345"), new BigDecimal("2.35"), 1, 0, Boolean.TRUE);
        for (Rule r : Rule.values()) {
            if (!r.isInjective()) {
                continue;
            }
            Map<Object, Object> seen = new HashMap<>();
            for (Object p : probes) {
                Object k = r.apply(p);
                Object prev = seen.put(k, p);
                if (prev != null && !prev.equals(p)) {
                    assertEquals(Rule.BOOLEAN, r,
                            r + " claims to be injective but maps " + prev + " and " + p + " together");
                }
            }
        }
    }

    @Test
    void rulesAreScopedSoTheyCannotConsumeEachOthersColumns() {
        Rule.Set all = Rule.Set.all();
        Object seen = all.canonicalise("seen", 1705309200000L);
        assertEquals("@1705309200000", seen,
                "NUMERIC must not reach the timestamp column; scoping is what prevents it");
    }

    @Test
    void aRuleDoesNothingToAColumnItDoesNotGovern() {
        Rule.Set trim = Rule.Set.of(Rule.TRIM);
        assertEquals(" 5 ", trim.canonicalise("amount", " 5 "));
        assertEquals("5", trim.canonicalise("name", " 5 "));
    }

    @Test
    void setsComposeInEnumOrderDeterministically() {
        Rule.Set a = Rule.Set.of(Rule.TRIM, Rule.CASEFOLD);
        Rule.Set b = Rule.Set.of(Rule.CASEFOLD, Rule.TRIM);
        assertEquals(a.canonicalise("name", " smith "), b.canonicalise("name", " smith "));
        assertEquals(a.label(), b.label());
    }

    @Test
    void plusAndMinusDoNotMutateTheOriginal() {
        Rule.Set base = Rule.Set.of(Rule.TRIM);
        Rule.Set more = base.plus(Rule.NFC);
        assertFalse(base.has(Rule.NFC));
        assertTrue(more.has(Rule.NFC));
        assertFalse(more.minus(Rule.NFC).has(Rule.NFC));
    }

    @Test
    void emptySetIsLabelledDistinctly() {
        assertEquals("(none)", Rule.Set.none().label());
    }

    @Test
    void allContainsEveryRule() {
        for (Rule r : Rule.values()) {
            assertTrue(Rule.Set.all().has(r));
        }
    }

    @Test
    void columnsIterateInDeclarationOrder() {
        // Regression. This was Set.of(), whose iteration order is randomised
        // per JVM by ImmutableCollections.SALT, so the injectivity table in
        // docs/results.md rendered [name, code] in one JVM and [code, name] in
        // the next. Any Set.of or Map.of whose iteration order reaches an
        // artifact is a latent coin flip; asserting the concrete order here is
        // cheap and would have caught it immediately.
        assertEquals(List.of("name", "code"), List.copyOf(Rule.TRIM.columns()));
        assertEquals(List.of("amount"), List.copyOf(Rule.NUMERIC.columns()));
        assertEquals(List.of("account"), List.copyOf(Rule.IDENTIFIER.columns()));
    }

    @Test
    void columnOrderIsStableAcrossRepeatedReads() {
        for (Rule r : Rule.values()) {
            List<String> first = List.copyOf(r.columns());
            for (int i = 0; i < 50; i++) {
                assertEquals(first, List.copyOf(r.columns()), r + " reordered its columns");
            }
        }
    }
}
