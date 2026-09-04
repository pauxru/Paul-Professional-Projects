// Package lexicon supplies synonymy, and nothing else.
//
// WHY THIS EXISTS, stated plainly because it is the project's biggest
// methodological compromise.
//
// This environment has no embedding API. The question the project asks is
// whether a similarity threshold can separate paraphrases (which a cache SHOULD
// serve from one another) from decisive-token variants (which it MUST NOT), and
// there are two dishonest ways to answer it:
//
//  1. Simulate the similarity scores directly. Then the answer is whatever
//     distribution I chose, and the experiment proves nothing.
//  2. Use raw TF-IDF and declare the failure. TF-IDF scores true paraphrases at
//     cosine 0.00 when they share no words, so it fails at BOTH halves of the
//     job. That is a finding about TF-IDF, not about semantic caching, and
//     everyone already knows it.
//
// So: TF-IDF cosine is used unchanged, and it is given the one capability a
// sentence embedder has that a bag of words lacks - knowing that "refund" and
// "money back" mean the same thing. That capability is supplied here, by hand,
// as an explicit and auditable table.
//
// The critical property, and the thing that makes the result mean something:
//
//	THE LEXICON CONTAINS NO INFORMATION ABOUT WHICH TOKENS ARE DECISIVE.
//
// "germany" and "france" are not in it. "pro" and "free" are not in it. "on"
// and "off", "import" and "export", "401" and "429" are not in it. They stay
// distinct tokens with high IDF, and they are averaged away by cosine exactly
// as they are in a real embedding - because cosine over an aggregated vector is
// a weighted mean, and a mean cannot express "this one token decides the
// answer". That failure is emergent from the arithmetic. It is not authored.
//
// docs/known-limitations.md states what this does and does not license.
package lexicon

import "strings"

// Canon maps a token to its concept. Unmapped tokens are their own concept.
func Canon(tok string) string {
	if c, ok := table[tok]; ok {
		return c
	}
	return tok
}

// CanonPhrase rewrites a two-word phrase if it is a known synonym, returning
// the replacement tokens and whether a rewrite happened. This handles the cases
// where the synonym is not a single word - "money back", "log in", "two factor".
func CanonPhrase(a, b string) ([]string, bool) {
	if c, ok := phrases[a+" "+b]; ok {
		return strings.Fields(c), true
	}
	return nil, false
}

// Size reports the number of mapped surface forms, quoted in the report so the
// reader can weigh how much hand-authoring is in play.
func Size() int { return len(table) + len(phrases) }

var phrases = map[string]string{
	"money back":     "refund",
	"log in":         "login",
	"logging in":     "login",
	"sign in":        "login",
	"signing in":     "login",
	"two factor":     "2fa",
	"multi factor":   "2fa",
	"single sign":    "sso",
	"sub processors": "subprocessor",
	"time limit":     "window",
	"real support":   "human",
}

// table is surface form -> concept. Grouped by concept for auditability: every
// line can be checked against the corpus by eye, which is the point of writing
// it out rather than learning it.
var table = map[string]string{
	// --- refund / return -------------------------------------------------
	"refund": "refund", "refunds": "refund", "refunded": "refund",
	"reimburse": "refund", "reimbursement": "refund",
	"exchange": "exchange", "exchanges": "exchange",
	"return": "return", "returns": "return",

	// --- time windows ----------------------------------------------------
	"window": "window", "period": "window", "deadline": "window",
	"days": "window", "duration": "window", "long": "window",
	"limit": "limit", "limits": "limit", "quota": "limit", "cap": "limit",
	"allowance": "limit",

	// --- requesting / asking ---------------------------------------------
	"ask": "request", "asking": "request", "request": "request",
	"requests": "request", "requesting": "request", "claim": "request",
	"need": "request", "want": "request", "wanted": "request",

	// --- cancellation vs enabling ----------------------------------------
	// NOTE: "on" and "off" are deliberately NOT mapped to anything. They are
	// the decisive tokens in the autorenew and 2FA families.
	"cancel": "cancel", "cancelling": "cancel", "cancellation": "cancel",
	"stop": "cancel", "end": "cancel", "terminate": "cancel",
	"disable": "disable", "deactivate": "disable",
	"enable": "enable", "activate": "enable",
	// "remove" is NOT a synonym of "disable". Removing a teammate and
	// disabling 2FA are different intents in different families, and
	// collapsing the verbs manufactures exactly the confusion this project
	// measures. The distinction was found by TestOppositesAreNotCollapsed's
	// stricter successor: see internal/lexicon/lexicon_test.go.
	"remove": "remove", "removing": "remove", "revoke": "remove",
	"kick": "remove", "deprovision": "remove",
	"close": "close", "shut": "close", "closing": "close",
	"delete": "delete", "erase": "delete", "erased": "delete",
	"wipe": "delete", "permanently": "delete",

	// --- renewal / subscription ------------------------------------------
	"renew": "renew", "renewal": "renew", "renewing": "renew",
	"renews": "renew", "automatic": "auto", "automatically": "auto",
	"subscription": "subscription", "plan": "plan", "plans": "plan",
	"tier": "plan", "package": "plan",

	// --- billing ---------------------------------------------------------
	"invoice": "invoice", "invoices": "invoice", "bill": "invoice",
	"billing": "invoice", "receipt": "invoice", "statement": "invoice",
	"pdf": "copy", "copy": "copy",
	"payment": "payment", "pay": "payment", "paying": "payment",
	"charge": "payment", "charged": "payment", "charges": "payment",
	"card": "card", "cards": "card",
	"failed": "failed", "declined": "failed", "rejected": "failed",
	"fail": "failed", "unsuccessful": "failed",
	"vat": "vat", "tax": "vat", "taxes": "vat",
	"fees": "cost", "fee": "cost", "cost": "cost", "costs": "cost",
	"much": "cost", "price": "cost", "pricing": "cost",

	// --- plan movement ---------------------------------------------------
	"upgrade": "upgrade", "higher": "upgrade", "bigger": "upgrade",
	"downgrade": "downgrade", "lower": "downgrade", "smaller": "downgrade",
	"switch": "switch", "move": "switch", "moving": "switch",
	"change": "change", "changing": "change", "update": "change",
	"updating": "change", "replace": "change", "different": "change",

	// --- shipping --------------------------------------------------------
	"shipping": "shipping", "delivery": "shipping", "deliver": "shipping",
	"postage": "shipping", "ship": "shipping", "arrive": "shipping",
	"arrives": "shipping", "arrived": "shipping", "dispatch": "shipping",
	"order": "order", "orders": "order", "parcel": "order",

	// --- data ------------------------------------------------------------
	"export": "export", "download": "export", "dump": "export",
	"import": "import", "upload": "import", "load": "import",
	"data": "data", "records": "data", "information": "data",
	"everything": "data", "info": "data",
	"residency": "residency", "stored": "residency", "store": "residency",

	// --- auth ------------------------------------------------------------
	"password": "password", "passwords": "password",
	"reset": "reset", "forgot": "reset", "forgotten": "reset",
	"rotate": "reset", "generate": "new", "new": "new",
	"api": "api", "key": "key", "keys": "key", "token": "key",
	"authentication": "auth", "auth": "auth", "authorised": "auth",
	"unauthorised": "auth", "unauthorized": "auth",
	"login": "login", "access": "access",

	// --- api errors ------------------------------------------------------
	// 401 and 429 stay distinct. They are the decisive tokens of that family.
	"error": "error", "errors": "error", "errored": "error",
	"throttled": "throttle", "throttling": "throttle",
	"rate": "rate", "many": "many", "too": "too", "keep": "keep",

	// --- webhooks --------------------------------------------------------
	"webhook": "webhook", "webhooks": "webhook", "callback": "webhook",
	"callbacks": "webhook", "events": "webhook", "event": "webhook",
	"firing": "firing", "fire": "firing", "receiving": "firing",
	"receive": "firing", "arriving": "firing", "endpoint": "endpoint",
	"retry": "retry", "retries": "retry", "redelivered": "retry",
	"redelivery": "retry", "policy": "policy", "times": "retry",

	// --- team ------------------------------------------------------------
	"add": "add", "invite": "add", "adding": "add",
	"team": "org", "workspace": "org", "organisation": "org",
	"organization": "org", "company": "org", "account": "account",
	"someone": "person", "colleague": "person", "user": "person",
	"person": "person", "users": "person", "human": "human",
	"people": "person", "else": "person",
	"ownership": "owner", "owner": "owner", "transfer": "owner",
	"make": "owner", "email": "email", "address": "email",

	// --- ops -------------------------------------------------------------
	"down": "outage", "outage": "outage", "offline": "outage",
	"timing": "outage", "timeout": "outage", "timeouts": "outage",
	"nothing": "outage", "service": "service", "works": "service",
	"working":     "service",
	"maintenance": "maintenance", "planned": "maintenance",
	"scheduled": "maintenance",
	"uptime":    "uptime", "sla": "sla", "guarantee": "uptime",
	"downtime": "uptime", "allowed": "uptime", "contract": "sla",
	"credits": "credits", "compensation": "credits", "breach": "credits",
	"status": "status", "check": "status", "showing": "status",
	"shows": "status", "page": "status",

	// --- compliance ------------------------------------------------------
	"gdpr": "gdpr", "dpa": "dpa", "paperwork": "dpa", "signed": "dpa",
	"agreement": "dpa", "processing": "dpa", "send": "send",
	"subprocessor": "subprocessor", "subprocessors": "subprocessor",
	"processors": "subprocessor", "parties": "subprocessor",
	"third": "subprocessor", "handle": "subprocessor", "list": "subprocessor",

	// --- identity --------------------------------------------------------
	"sso": "sso", "saml": "sso", "identity": "idp", "provider": "idp",
	"configure": "setup", "setup": "setup", "set": "setup", "up": "setup",
	"connect": "setup", "connecting": "setup",
	"scim": "scim", "provisioning": "scim", "sync": "scim",

	// --- clients ---------------------------------------------------------
	"mobile": "mobile", "app": "mobile", "iphone": "mobile",
	"android": "mobile", "ios": "mobile", "phone": "mobile",
	"browser": "browser", "browsers": "browser", "safari": "browser",
	"chrome": "browser", "firefox": "browser", "explorer": "browser",
	"internet":  "browser",
	"supported": "support", "support": "support", "supports": "support",

	// --- misc ------------------------------------------------------------
	"talk": "contact", "reach": "contact", "contact": "contact",
	"speak": "contact", "bot": "bot", "answers": "answer",
	"answer": "answer", "yet": "pending", "still": "pending",
	"giving": "answer", "use": "use", "using": "use", "work": "service",
}
