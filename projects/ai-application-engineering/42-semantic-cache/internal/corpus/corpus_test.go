package corpus

import (
	"sort"
	"strings"
	"testing"
)

func TestEveryQueryIsLabelled(t *testing.T) {
	for _, q := range All() {
		if strings.TrimSpace(q.Text) == "" {
			t.Errorf("empty text for intent %q", q.Intent)
		}
		if q.Intent == "" {
			t.Errorf("query %q has no intent", q.Text)
		}
		if q.Tenant == "" {
			t.Errorf("query %q has no tenant", q.Text)
		}
	}
}

func TestIntentsAreCoveredByAtLeastTwoPhrasings(t *testing.T) {
	counts := map[string]int{}
	for _, q := range All() {
		counts[q.Intent]++
	}
	for _, it := range Intents() {
		if counts[it] < 2 {
			t.Errorf("intent %q has %d phrasings; a single phrasing cannot test paraphrase recall",
				it, counts[it])
		}
	}
}

// A family with one member is not a trap, and a family that silently lost a
// member would weaken every measurement in section 1 without failing anything.
func TestEveryFamilyHasAtLeastTwoMembers(t *testing.T) {
	fams := map[string][]string{}
	for _, it := range Intents() {
		if f := Family(it); f != "" {
			fams[f] = append(fams[f], it)
		}
	}
	if len(fams) == 0 {
		t.Fatal("no confusable families defined")
	}
	for f, members := range fams {
		if len(members) < 2 {
			t.Errorf("family %q has only %v", f, members)
		}
	}
}

func TestFamilyMembershipIsAFunction(t *testing.T) {
	// Family() is a map lookup, so an intent cannot be in two families by
	// construction - but the map literal could name the same intent twice with
	// different families and Go would silently keep the last. This catches the
	// consequence: every member of a family must agree on the family name.
	for _, it := range Intents() {
		f := Family(it)
		if f == "" {
			continue
		}
		for _, other := range Intents() {
			if Family(other) == f && !ConfusableIntents(it, other) && it != other {
				t.Errorf("%q and %q share family %q but are not confusable", it, other, f)
			}
		}
	}
}

func TestConfusableIsSymmetricAndIrreflexive(t *testing.T) {
	its := Intents()
	for _, a := range its {
		if ConfusableIntents(a, a) {
			t.Errorf("%q is confusable with itself", a)
		}
		for _, b := range its {
			if ConfusableIntents(a, b) != ConfusableIntents(b, a) {
				t.Errorf("Confusable(%q,%q) != Confusable(%q,%q)", a, b, b, a)
			}
		}
	}
}

func TestEveryIntentHasADistinctAnswer(t *testing.T) {
	seen := map[string]string{}
	for _, it := range Intents() {
		a := Answer(it)
		if prev, ok := seen[a]; ok {
			t.Errorf("intents %q and %q share the answer %q, so confusing them would be unobservable",
				prev, it, a)
		}
		seen[a] = it
	}
}

// The whole method depends on adversarial queries being genuinely close to
// their siblings. If an "adversarial" group has no family, it is just another
// query and the label is a lie.
func TestAdversarialQueriesBelongToAFamily(t *testing.T) {
	for _, q := range Adversarial() {
		if Family(q.Intent) == "" {
			t.Errorf("intent %q is marked adversarial but has no confusable family", q.Intent)
		}
	}
}

// The multi-tenant trap only works if the wording really is identical. If a
// future edit makes the three variants differ, section 5's claim - that no
// encoder can separate them - silently becomes false.
func TestTheTenantTrapIsWordedIdentically(t *testing.T) {
	byIntent := map[string][]string{}
	for _, q := range All() {
		if Family(q.Intent) == "tenant-plan" {
			byIntent[q.Intent] = append(byIntent[q.Intent], q.Text)
		}
	}
	if len(byIntent) != 3 {
		t.Fatalf("expected 3 tenant-plan intents, got %d", len(byIntent))
	}
	var ref []string
	refIntent := ""
	tenants := map[string]bool{}
	for _, q := range All() {
		if Family(q.Intent) == "tenant-plan" {
			tenants[q.Tenant] = true
		}
	}
	if len(tenants) != 3 {
		t.Errorf("the tenant trap spans %d tenants, want 3", len(tenants))
	}
	for it, texts := range byIntent {
		sort.Strings(texts)
		if ref == nil {
			ref, refIntent = texts, it
			continue
		}
		if strings.Join(texts, "|") != strings.Join(ref, "|") {
			t.Errorf("tenant-plan wording differs between %q and %q:\n  %v\n  %v",
				refIntent, it, ref, texts)
		}
	}
}

func TestSharedIntentsExistAndAreNotAllOfThem(t *testing.T) {
	n := 0
	for _, it := range Intents() {
		if Shared(it) {
			n++
		}
	}
	if n == 0 {
		t.Fatal("no tenant-independent intents; section 5 would have nothing to trade off")
	}
	if n == len(Intents()) {
		t.Fatal("every intent is tenant-independent; tenant scoping would be free")
	}
}

// A shared intent whose answer varies by tenant would make cross-tenant reuse
// wrong even when the report says it is right.
func TestTheTenantTrapIsNotShared(t *testing.T) {
	for _, it := range Intents() {
		if Family(it) == "tenant-plan" && Shared(it) {
			t.Errorf("%q is both a tenant trap and marked tenant-independent", it)
		}
	}
}

func TestAllIsStableAcrossCalls(t *testing.T) {
	a, b := All(), All()
	if len(a) != len(b) {
		t.Fatalf("All() returned %d then %d queries", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("All() is not stable at index %d: %+v vs %+v", i, a[i], b[i])
		}
	}
}

func TestIntentsIsSorted(t *testing.T) {
	its := Intents()
	if !sort.StringsAreSorted(its) {
		t.Error("Intents() must be sorted; unsorted output would leak map order into the workload")
	}
}

func TestNoDuplicateQueryTextWithinAnIntent(t *testing.T) {
	seen := map[string]bool{}
	for _, q := range All() {
		k := q.Intent + "\x00" + q.Text
		if seen[k] {
			t.Errorf("duplicate text %q under intent %q inflates that intent's paraphrase count",
				q.Text, q.Intent)
		}
		seen[k] = true
	}
}

// Identical text under two DIFFERENT intents is allowed exactly once: the
// tenant trap. Anywhere else it is a labelling mistake that would show up as an
// unfixable false hit.
func TestIdenticalTextAcrossIntentsIsOnlyTheTenantTrap(t *testing.T) {
	byText := map[string][]string{}
	for _, q := range All() {
		byText[strings.ToLower(q.Text)] = append(byText[strings.ToLower(q.Text)], q.Intent)
	}
	for text, its := range byText {
		uniq := map[string]bool{}
		for _, it := range its {
			uniq[it] = true
		}
		if len(uniq) < 2 {
			continue
		}
		for it := range uniq {
			if Family(it) != "tenant-plan" {
				t.Errorf("text %q is shared by intent %q outside the tenant trap", text, it)
			}
		}
	}
}
