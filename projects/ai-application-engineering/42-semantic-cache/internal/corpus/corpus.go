// Package corpus holds a hand-written, hand-labelled set of support queries.
//
// It is written by hand rather than generated, and that is the most important
// design decision in this project. A generated corpus can only exhibit the
// failure modes its generator was built to exhibit, so measuring a semantic
// cache against one proves whatever you assumed. These are the kinds of things
// people actually type at a support box, including the pairs that break
// similarity search — and they break it for reasons that are properties of
// English, not properties of my generator.
//
// Labels:
//
//	Intent      two queries may share a cached answer IFF their intents match.
//	Adversarial this query is lexically close to a query with a DIFFERENT
//	            intent. These are the traps: a decisive token (an entity, a
//	            number, a negation, a direction) changes the answer while
//	            leaving 80-90% of the words identical.
//
// The adversarial set is not a curiosity. It is the entire risk surface of a
// semantic cache, and reporting an aggregate false-hit rate over a corpus
// without one is how a cache ships with a 2% measured error rate and a 40%
// error rate on the queries that matter.
package corpus

import "sort"

// Query is one utterance with its ground-truth intent.
type Query struct {
	Text   string
	Intent string
	Tenant string
	// Adversarial marks a query that is lexically near a different intent.
	Adversarial bool
}

// All returns the corpus. Deterministic order; do not reorder casually, the
// report quotes counts.
func All() []Query {
	qs := make([]Query, 0, 256)
	for _, g := range groups {
		for _, t := range g.texts {
			qs = append(qs, Query{Text: t, Intent: g.intent, Tenant: g.tenant, Adversarial: g.adversarial})
		}
	}
	return qs
}

// Intents returns the distinct intents, sorted.
func Intents() []string {
	seen := map[string]bool{}
	for _, g := range groups {
		seen[g.intent] = true
	}
	out := make([]string, 0, len(seen))
	for k := range seen {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// Adversarial returns only the trap queries.
func Adversarial() []Query {
	var out []Query
	for _, q := range All() {
		if q.Adversarial {
			out = append(out, q)
		}
	}
	return out
}

// families groups intents that are deliberately confusable: same shape, same
// vocabulary, different answer. Membership is the ground truth for "this pair
// is a trap", and it is what lets the report score the adversarial subset
// separately instead of drowning it in an average.
//
// Every family is one of the five ways English changes an answer without
// changing many words:
//
//	entity     germany / france / us,  uk / ireland
//	tier       free / pro / enterprise
//	polarity   turn on / turn off, enable / disable
//	direction  import / export, upgrade / downgrade, add / remove
//	code       401 / 429
var families = map[string]string{
	"refund-window": "refund", "refund-window-digital": "refund", "exchange-window": "refund",
	"cancel-autorenew": "renewal", "enable-autorenew": "renewal", "cancel-subscription": "renewal",
	"invoice-download": "invoice", "invoice-vat": "invoice",
	"payment-failed": "payment", "change-card": "payment",
	"limits-free": "limits", "limits-pro": "limits", "limits-enterprise": "limits",
	"upgrade-plan": "plan-move", "downgrade-plan": "plan-move",
	"shipping-germany": "shipping-region", "shipping-france": "shipping-region",
	"shipping-us": "shipping-region",
	"tax-rate-uk": "tax-region", "tax-rate-ireland": "tax-region",
	"export-data": "data-move", "import-data": "data-move", "delete-data": "data-move",
	"reset-password": "credential", "rotate-api-key": "credential",
	"enable-2fa": "2fa", "disable-2fa": "2fa",
	"rate-limit-429": "api-error", "auth-401": "api-error",
	"webhook-not-firing": "webhook", "webhook-retry": "webhook",
	"add-teammate": "team", "remove-teammate": "team", "transfer-ownership": "team",
	"status-outage": "availability", "maintenance-window": "availability",
	"sla-uptime": "sla", "sla-credits": "sla",
	"gdpr-dpa": "compliance", "subprocessors": "compliance", "data-residency": "compliance",
	"sso-setup": "identity", "scim-provisioning": "identity",
	"plan-limit-acme": "tenant-plan", "plan-limit-globex": "tenant-plan",
	"plan-limit-initech": "tenant-plan",
}

// sharedIntents are the questions whose answer is a property of the PLATFORM,
// not of the asking customer: status, policy, protocol, how-to. Every tenant
// may ask them and every tenant deserves the same answer, so an unscoped cache
// can legitimately serve one tenant from another's entry.
//
// Everything not listed here is account-specific - the answer is that
// customer's plan, contract, region or data - and cross-tenant reuse is a
// leak. The split is what makes tenant scoping a TRADE-OFF rather than an
// obvious win: scoping caches each shared answer once per tenant.
var sharedIntents = map[string]bool{
	"status-outage": true, "maintenance-window": true,
	"sla-uptime": true, "sla-credits": true,
	"gdpr-dpa": true, "subprocessors": true, "data-residency": true,
	"mobile-app": true, "browser-support": true, "contact-human": true,
	"reset-password": true, "enable-2fa": true, "disable-2fa": true,
	"rotate-api-key": true, "sso-setup": true, "scim-provisioning": true,
	"rate-limit-429": true, "auth-401": true,
	"webhook-not-firing": true, "webhook-retry": true,
}

// Shared reports whether an intent's answer is tenant-independent.
func Shared(intent string) bool { return sharedIntents[intent] }

// Tenants is the fixed tenant set, in a stable order.
func Tenants() []string { return []string{"acme", "globex", "initech"} }

// Family returns the confusable family of an intent, or "" if it has no
// designed trap partner.
func Family(intent string) string { return families[intent] }

// Confusable reports whether two queries are a designed trap: different
// answers, same family. A cache that serves one from the other is not making a
// small mistake - it is answering a question about Germany with the policy for
// France.
func Confusable(a, b Query) bool { return ConfusableIntents(a.Intent, b.Intent) }

// ConfusableIntents is Confusable on bare intent labels, for callers that have
// scored an outcome rather than compared two queries.
func ConfusableIntents(a, b string) bool {
	if a == b {
		return false
	}
	fa, fb := families[a], families[b]
	return fa != "" && fa == fb
}

// Answer is the canonical answer text for an intent. Content is irrelevant to
// every measurement; what matters is that two intents have DIFFERENT answers,
// so serving one for the other is observably wrong.
func Answer(intent string) string { return "[answer for " + intent + "]" }

type group struct {
	intent      string
	tenant      string
	adversarial bool
	texts       []string
}

// Each group is one intent: every text in it has the same correct answer, so a
// cache SHOULD serve any of them from any other. Groups marked adversarial sit
// deliberately close to a sibling group whose answer is different.
var groups = []group{
	// ---------------------------------------------------------------- refunds
	{intent: "refund-window", tenant: "acme", texts: []string{
		"what is your refund window",
		"how long do I have to ask for a refund",
		"how many days do I get to request my money back",
		"is there a time limit on refunds",
		"refund window length",
	}},
	{intent: "refund-window-digital", tenant: "acme", adversarial: true, texts: []string{
		"what is your refund window for digital goods",
		"how long do I have to ask for a refund on a digital purchase",
		"is there a time limit on refunds for downloads",
	}},
	{intent: "exchange-window", tenant: "acme", adversarial: true, texts: []string{
		"what is your exchange window",
		"how long do I have to ask for an exchange",
		"is there a time limit on exchanges",
	}},
	{intent: "refund-status", tenant: "acme", texts: []string{
		"where is my refund",
		"my refund has not arrived yet",
		"how do I check the status of a refund",
		"refund still not showing on my card",
	}},

	// ---------------------------------------------------------------- billing
	{intent: "cancel-autorenew", tenant: "acme", texts: []string{
		"how do I cancel auto renew",
		"turn off automatic renewal",
		"I want to stop my subscription renewing",
		"disable auto renewal on my plan",
	}},
	{intent: "enable-autorenew", tenant: "acme", adversarial: true, texts: []string{
		"how do I turn on auto renew",
		"enable automatic renewal",
		"I want my subscription to renew automatically",
	}},
	{intent: "cancel-subscription", tenant: "acme", adversarial: true, texts: []string{
		"how do I cancel my subscription",
		"I want to close my subscription entirely",
		"cancel my plan completely",
	}},
	{intent: "invoice-download", tenant: "acme", texts: []string{
		"where do I download my invoice",
		"how do I get a copy of my invoice",
		"I need a pdf of my bill",
		"send me last month's invoice",
	}},
	{intent: "invoice-vat", tenant: "acme", adversarial: true, texts: []string{
		"how do I add my VAT number to my invoice",
		"my invoice is missing the VAT number",
		"I need a VAT invoice for my company",
	}},
	{intent: "payment-failed", tenant: "acme", texts: []string{
		"my payment failed",
		"card declined when paying",
		"the charge did not go through",
		"why was my payment rejected",
	}},
	{intent: "change-card", tenant: "acme", adversarial: true, texts: []string{
		"how do I change the card on my account",
		"update my payment method",
		"I need to replace my card details",
	}},

	// ------------------------------------------------------------ plan limits
	{intent: "limits-free", tenant: "acme", adversarial: true, texts: []string{
		"what is the request limit on the free plan",
		"how many requests do I get on free",
		"free plan quota",
	}},
	{intent: "limits-pro", tenant: "acme", adversarial: true, texts: []string{
		"what is the request limit on the pro plan",
		"how many requests do I get on pro",
		"pro plan quota",
	}},
	{intent: "limits-enterprise", tenant: "acme", adversarial: true, texts: []string{
		"what is the request limit on the enterprise plan",
		"how many requests do I get on enterprise",
		"enterprise plan quota",
	}},
	{intent: "upgrade-plan", tenant: "acme", texts: []string{
		"how do I upgrade my plan",
		"I want to move to a bigger plan",
		"switch me to a higher tier",
		"upgrade my account",
	}},
	{intent: "downgrade-plan", tenant: "acme", adversarial: true, texts: []string{
		"how do I downgrade my plan",
		"I want to move to a smaller plan",
		"switch me to a lower tier",
	}},

	// ------------------------------------------------------------ regionality
	{intent: "shipping-germany", tenant: "globex", adversarial: true, texts: []string{
		"how long does shipping take to Germany",
		"delivery time for Germany",
		"when will my order arrive in Germany",
	}},
	{intent: "shipping-france", tenant: "globex", adversarial: true, texts: []string{
		"how long does shipping take to France",
		"delivery time for France",
		"when will my order arrive in France",
	}},
	{intent: "shipping-us", tenant: "globex", adversarial: true, texts: []string{
		"how long does shipping take to the United States",
		"delivery time for the US",
		"when will my order arrive in the US",
	}},
	{intent: "shipping-cost", tenant: "globex", texts: []string{
		"how much does shipping cost",
		"what are your delivery charges",
		"is postage free",
		"shipping fees",
	}},
	{intent: "tax-rate-uk", tenant: "globex", adversarial: true, texts: []string{
		"what is the tax rate for orders in the UK",
		"how much VAT is charged in the UK",
	}},
	{intent: "tax-rate-ireland", tenant: "globex", adversarial: true, texts: []string{
		"what is the tax rate for orders in Ireland",
		"how much VAT is charged in Ireland",
	}},

	// ------------------------------------------------------------- data / api
	{intent: "export-data", tenant: "initech", texts: []string{
		"how do I export my data",
		"I need to download everything in my account",
		"get a dump of my records",
		"export all my information",
	}},
	{intent: "import-data", tenant: "initech", adversarial: true, texts: []string{
		"how do I import my data",
		"I need to upload my records into my account",
		"bulk load my information",
	}},
	{intent: "delete-data", tenant: "initech", adversarial: true, texts: []string{
		"how do I delete my data",
		"I need everything in my account erased",
		"wipe all my information",
	}},
	{intent: "reset-password", tenant: "initech", texts: []string{
		"how do I reset my password",
		"I forgot my password",
		"send me a password reset link",
		"cannot log in, need a new password",
	}},
	{intent: "rotate-api-key", tenant: "initech", adversarial: true, texts: []string{
		"how do I reset my API key",
		"I need to rotate my API key",
		"generate a new API key",
	}},
	{intent: "enable-2fa", tenant: "initech", texts: []string{
		"how do I turn on two factor authentication",
		"enable 2FA on my account",
		"set up multi factor login",
	}},
	{intent: "disable-2fa", tenant: "initech", adversarial: true, texts: []string{
		"how do I turn off two factor authentication",
		"disable 2FA on my account",
		"remove multi factor login",
	}},
	{intent: "rate-limit-429", tenant: "initech", texts: []string{
		"why am I getting 429 errors",
		"I keep hitting rate limits",
		"too many requests error",
		"my API calls are being throttled",
	}},
	{intent: "auth-401", tenant: "initech", adversarial: true, texts: []string{
		"why am I getting 401 errors",
		"my API calls are being rejected as unauthorised",
		"unauthorized error on every request",
	}},
	{intent: "webhook-not-firing", tenant: "initech", texts: []string{
		"my webhooks are not firing",
		"I am not receiving webhook events",
		"webhook endpoint gets nothing",
		"no callbacks arriving",
	}},
	{intent: "webhook-retry", tenant: "initech", adversarial: true, texts: []string{
		"how many times do you retry a failed webhook",
		"what is the webhook retry policy",
		"do webhooks get redelivered",
	}},

	// ---------------------------------------------------------------- account
	{intent: "add-teammate", tenant: "acme", texts: []string{
		"how do I add someone to my team",
		"invite a colleague to my workspace",
		"add a user to my organisation",
	}},
	{intent: "remove-teammate", tenant: "acme", adversarial: true, texts: []string{
		"how do I remove someone from my team",
		"revoke a colleague's access to my workspace",
		"delete a user from my organisation",
	}},
	{intent: "transfer-ownership", tenant: "acme", adversarial: true, texts: []string{
		"how do I transfer ownership of my organisation",
		"make someone else the owner of my workspace",
	}},
	{intent: "change-email", tenant: "acme", texts: []string{
		"how do I change my email address",
		"update the email on my account",
		"I want a different email on file",
	}},
	{intent: "close-account", tenant: "acme", texts: []string{
		"how do I close my account",
		"I want to delete my account permanently",
		"shut down my account for good",
	}},

	// ------------------------------------------------------------- operations
	{intent: "status-outage", tenant: "globex", texts: []string{
		"is the service down",
		"are you having an outage",
		"everything is timing out, is it you or me",
		"status page shows green but nothing works",
	}},
	{intent: "maintenance-window", tenant: "globex", adversarial: true, texts: []string{
		"when is your scheduled maintenance window",
		"are you doing planned maintenance",
		"when will the service be down for maintenance",
	}},
	{intent: "sla-uptime", tenant: "globex", texts: []string{
		"what uptime do you guarantee",
		"what is your SLA",
		"how much downtime is allowed under contract",
	}},
	{intent: "sla-credits", tenant: "globex", adversarial: true, texts: []string{
		"how do I claim SLA credits",
		"what compensation do I get if you breach the SLA",
		"service credits process",
	}},

	// ----------------------------------------------------------- long tail
	{intent: "gdpr-dpa", tenant: "globex", texts: []string{
		"do you have a data processing agreement",
		"I need a signed DPA",
		"send me your GDPR paperwork",
	}},
	{intent: "subprocessors", tenant: "globex", adversarial: true, texts: []string{
		"who are your sub processors",
		"which third parties handle our data",
		"list of subprocessors",
	}},
	{intent: "data-residency", tenant: "globex", adversarial: true, texts: []string{
		"where is our data stored",
		"can we keep our data in the EU",
		"data residency options",
	}},
	{intent: "sso-setup", tenant: "initech", texts: []string{
		"how do I set up SSO",
		"configure SAML single sign on",
		"connect my identity provider",
	}},
	{intent: "scim-provisioning", tenant: "initech", adversarial: true, texts: []string{
		"how do I set up SCIM",
		"configure automatic user provisioning",
		"sync users from my identity provider",
	}},
	{intent: "mobile-app", tenant: "acme", texts: []string{
		"do you have a mobile app",
		"is there an iphone app",
		"can I use this on android",
	}},
	{intent: "browser-support", tenant: "acme", texts: []string{
		"which browsers do you support",
		"does it work on safari",
		"is internet explorer supported",
	}},
	{intent: "contact-human", tenant: "acme", texts: []string{
		"I want to talk to a person",
		"get me a human",
		"how do I reach real support",
		"stop giving me bot answers",
	}},

	// The multi-tenant trap. Three tenants ask the SAME question in almost the
	// same words and must receive three different answers, because the answer
	// is their contract. No amount of semantic similarity can distinguish
	// these, because they are not semantically different - they differ only in
	// who is asking. This is the one confusable family that cannot be fixed by
	// a better embedding, only by scoping, which is why it is here.
	{intent: "plan-limit-acme", tenant: "acme", adversarial: true, texts: []string{
		"what is my monthly api request limit",
		"how many api calls do I get per month",
		"monthly request quota on my plan",
	}},
	{intent: "plan-limit-globex", tenant: "globex", adversarial: true, texts: []string{
		"what is my monthly api request limit",
		"how many api calls do I get per month",
		"monthly request quota on my plan",
	}},
	{intent: "plan-limit-initech", tenant: "initech", adversarial: true, texts: []string{
		"what is my monthly api request limit",
		"how many api calls do I get per month",
		"monthly request quota on my plan",
	}},
}
