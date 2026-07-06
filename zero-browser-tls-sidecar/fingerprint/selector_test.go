package fingerprint

import "testing"

// Verify that Select is deterministic: same seed + same mode => same template.
func TestSelectDeterministic(t *testing.T) {
	cases := []struct{ seed, mode string }{
		{"profile-A", "chrome"},
		{"profile-A", "firefox"},
		{"profile-A", "safari"},
		{"profile-A", "edge"},
		{"profile-A", "randomized"},
		{"profile-B", "randomized"},
		{"profile-C", "randomized"},
		{"", "chrome"},
	}
	for _, c := range cases {
		a, err := Select(c.seed, c.mode)
		if err != nil {
			t.Fatalf("Select(%q,%q) err: %v", c.seed, c.mode, err)
		}
		for i := 0; i < 10; i++ {
			b, err := Select(c.seed, c.mode)
			if err != nil {
				t.Fatalf("Select(%q,%q) iteration %d err: %v", c.seed, c.mode, i, err)
			}
			if b.ID != a.ID {
				t.Fatalf("non-deterministic: seed=%q mode=%q got %q then %q", c.seed, c.mode, a.ID, b.ID)
			}
		}
	}
}

// Verify that different seeds with mode=randomized produce a distribution
// of templates (not stuck on one).
func TestSelectRandomizedDistributes(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 50; i++ {
		seed := "seed-" + string(rune('a'+i%26)) + string(rune('0'+i))
		tpl, err := Select(seed, "randomized")
		if err != nil {
			t.Fatalf("Select(%q) err: %v", seed, err)
		}
		seen[tpl.ID] = true
	}
	if len(seen) < 2 {
		t.Fatalf("randomized mode stuck on one template: %v", seen)
	}
}

// Verify hashSeedMod edge cases.
func TestHashSeedMod(t *testing.T) {
	if hashSeedMod("anything", 0) != 0 {
		t.Fatal("hashSeedMod(_,0) must return 0")
	}
	if hashSeedMod("anything", -1) != 0 {
		t.Fatal("hashSeedMod(_,-1) must return 0")
	}
	for n := 1; n <= 32; n++ {
		got := hashSeedMod("seed-x", n)
		if got < 0 || got >= n {
			t.Fatalf("hashSeedMod out of range: n=%d got=%d", n, got)
		}
	}
}
