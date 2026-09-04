package dev.migver;

import java.math.BigDecimal;

/**
 * One row of the table being migrated, plus the hazard it was constructed to
 * exercise.
 *
 * <p>Carrying the hazard on the row is what makes the confusion matrix possible.
 * Without it a mismatch is just a mismatch; with it, every mismatch can be
 * attributed to a mechanism, and -- more importantly -- every *non*-mismatch on a
 * row that was built to be dangerous can be counted as a false negative.
 *
 * @param hazard the mechanism this row exercises, or null for an ordinary row
 * @param corrupting whether the hazard actually destroys information here, as
 *                   opposed to merely changing how the value renders
 */
public record Row(
        long id,
        String name,
        String code,
        String account,
        BigDecimal amount,
        Boolean active,
        String seen,
        Hazard hazard,
        boolean corrupting) {

    public static Row benign(long id, String name) {
        return new Row(id, name, "OK", "1000" + id, new BigDecimal("10.00"), true,
                "2024-01-15 09:00:00", null, false);
    }

    public Row withHazard(Hazard h, boolean corrupting) {
        return new Row(id, name, code, account, amount, active, seen, h, corrupting);
    }
}
