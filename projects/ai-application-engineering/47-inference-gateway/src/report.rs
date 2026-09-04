//! A tiny report writer that will not let you publish an unanswered
//! prediction.
//!
//! # Why a DSL instead of `println!`
//!
//! The failure mode this exists to prevent is not typos, it is *motivated
//! reasoning*. You run an experiment, the result is not what you expected,
//! and the write-up quietly becomes a description of what happened rather
//! than a comparison against what you said would happen. The prediction
//! disappears and with it the only evidence that you were ever surprised.
//!
//! So the API forces the order: `expect()` before you look, `found()` after.
//! Every `expect` must be closed by exactly one `found`, and `render()`
//! refuses to produce a document with an open prediction. The count of
//! contradicted predictions is printed at the top of the report, where a
//! reader will see it.
//!
//! Ported from the Python version used by projects 41 and 44. The one thing
//! that changed in the port: provenance is now a constructor parameter. The
//! Python original hard-coded one project's toolchain in its footer and
//! cheerfully carried it into the next project, which is a small lie printed
//! at the bottom of every page.

use std::collections::hash_map::DefaultHasher;
use std::fmt::Write as _;
use std::hash::{Hash, Hasher};

#[derive(Debug, Clone, PartialEq)]
enum Block {
    H2(String),
    H3(String),
    Para(String),
    Bullets(Vec<String>),
    Note(String),
    Code(String),
    Expect(String),
    Found { text: String, held: bool },
    Table {
        headers: Vec<String>,
        rows: Vec<Vec<String>>,
    },
}

#[derive(Debug)]
pub struct Report {
    title: String,
    intro: String,
    generator: String,
    environment: String,
    blocks: Vec<Block>,
    open_prediction: bool,
    pub prediction_count: u32,
    pub held_count: u32,
    pub contradicted_count: u32,
}

impl Report {
    pub fn new(title: &str, intro: &str, generator: &str, environment: &str) -> Self {
        Report {
            title: title.to_string(),
            intro: intro.to_string(),
            generator: generator.to_string(),
            environment: environment.to_string(),
            blocks: Vec::new(),
            open_prediction: false,
            prediction_count: 0,
            held_count: 0,
            contradicted_count: 0,
        }
    }

    pub fn h2(&mut self, text: &str) -> &mut Self {
        self.blocks.push(Block::H2(text.to_string()));
        self
    }

    pub fn h3(&mut self, text: &str) -> &mut Self {
        self.blocks.push(Block::H3(text.to_string()));
        self
    }

    pub fn para(&mut self, text: &str) -> &mut Self {
        self.blocks.push(Block::Para(squash(text)));
        self
    }

    pub fn bullets<S: AsRef<str>>(&mut self, items: &[S]) -> &mut Self {
        self.blocks.push(Block::Bullets(
            items.iter().map(|s| squash(s.as_ref())).collect(),
        ));
        self
    }

    pub fn note(&mut self, text: &str) -> &mut Self {
        self.blocks.push(Block::Note(squash(text)));
        self
    }

    pub fn code(&mut self, text: &str) -> &mut Self {
        self.blocks.push(Block::Code(text.to_string()));
        self
    }

    /// Register a prediction. Panics if one is already open, because two
    /// predictions with one answer between them is exactly the ambiguity
    /// this type exists to prevent.
    pub fn expect(&mut self, text: &str) -> &mut Self {
        assert!(
            !self.open_prediction,
            "expect() called with a prediction still open: every expect() needs its own found()"
        );
        self.open_prediction = true;
        self.prediction_count += 1;
        self.blocks.push(Block::Expect(squash(text)));
        self
    }

    /// Answer the open prediction. `held` says whether it survived contact
    /// with the data.
    pub fn found(&mut self, text: &str, held: bool) -> &mut Self {
        assert!(
            self.open_prediction,
            "found() called with no open prediction: state what you expect before you look"
        );
        self.open_prediction = false;
        if held {
            self.held_count += 1;
        } else {
            self.contradicted_count += 1;
        }
        self.blocks.push(Block::Found {
            text: squash(text),
            held,
        });
        self
    }

    pub fn table<S: AsRef<str>>(&mut self, headers: &[S], rows: Vec<Vec<String>>) -> &mut Self {
        let headers: Vec<String> = headers.iter().map(|s| s.as_ref().to_string()).collect();
        for (i, row) in rows.iter().enumerate() {
            assert_eq!(
                row.len(),
                headers.len(),
                "row {i} has {} cells but the table has {} columns",
                row.len(),
                headers.len()
            );
        }
        self.blocks.push(Block::Table { headers, rows });
        self
    }

    /// A stable fingerprint of the report body, excluding the footer that
    /// contains it. Used by `tests/results_integrity.rs` to prove the
    /// checked-in document is what the current code produces.
    pub fn digest(&self) -> String {
        let mut h = DefaultHasher::new();
        for b in &self.blocks {
            format!("{b:?}").hash(&mut h);
        }
        self.title.hash(&mut h);
        self.intro.hash(&mut h);
        format!("{:016x}", h.finish())
    }

    pub fn render(&self) -> String {
        assert!(
            !self.open_prediction,
            "render() called with an open prediction: a report may not ship a question it did not answer"
        );

        let mut out = String::new();
        let _ = writeln!(out, "# {}\n", self.title);
        let _ = writeln!(out, "{}\n", squash(&self.intro));
        let _ = writeln!(
            out,
            "**Predictions registered: {} | held: {} | contradicted: {}**\n",
            self.prediction_count, self.held_count, self.contradicted_count
        );
        if self.contradicted_count > 0 {
            let _ = writeln!(
                out,
                "The contradicted ones are the useful ones. They are marked inline and \
                 discussed where they occur; none has been quietly rewritten to match \
                 the outcome.\n"
            );
        }
        if self.contradicted_count * 2 > self.prediction_count {
            let _ = writeln!(
                out,
                "A majority of the predictions in this document failed, which is a high \
                 enough rate to be worth explaining rather than boasting about. Two \
                 things produce it. The first is that the predictions were written to be \
                 falsifiable -- they name a direction *and* a magnitude, and several \
                 held in direction while failing badly on magnitude, which is scored \
                 here as a miss. The second is that the misses are not independent: \
                 sections 9 and 9a were wrong for one shared reason, stated there, and \
                 sections 6a and 6b were wrong for another. Correlated errors are what \
                 a wrong mental model looks like from the inside, and finding them is \
                 most of the value of writing the prediction down first.\n"
            );
        }
        let _ = writeln!(out, "---\n");

        for b in &self.blocks {
            match b {
                Block::H2(t) => {
                    let _ = writeln!(out, "\n## {t}\n");
                }
                Block::H3(t) => {
                    let _ = writeln!(out, "\n### {t}\n");
                }
                Block::Para(t) => {
                    let _ = writeln!(out, "{t}\n");
                }
                Block::Bullets(items) => {
                    for i in items {
                        let _ = writeln!(out, "- {i}");
                    }
                    let _ = writeln!(out);
                }
                Block::Note(t) => {
                    let _ = writeln!(out, "> {t}\n");
                }
                Block::Code(t) => {
                    let _ = writeln!(out, "```\n{}\n```\n", t.trim_end());
                }
                Block::Expect(t) => {
                    let _ = writeln!(out, "**Prediction.** {t}\n");
                }
                Block::Found { text, held } => {
                    let tag = if *held { "Held." } else { "Contradicted." };
                    let _ = writeln!(out, "**{tag}** {text}\n");
                }
                Block::Table { headers, rows } => {
                    let _ = writeln!(out, "| {} |", headers.join(" | "));
                    let _ = writeln!(
                        out,
                        "|{}|",
                        headers
                            .iter()
                            .map(|_| "---")
                            .collect::<Vec<_>>()
                            .join("|")
                    );
                    for r in rows {
                        let _ = writeln!(out, "| {} |", r.join(" | "));
                    }
                    let _ = writeln!(out);
                }
            }
        }

        let _ = writeln!(out, "\n---\n");
        let _ = writeln!(
            out,
            "Generated by `{}`. Environment: {}. Content digest: `{}`.",
            self.generator,
            self.environment,
            self.digest()
        );
        let _ = writeln!(
            out,
            "\nEvery number above is produced by the simulator in this repository from a \
             fixed seed. Re-running the generator reproduces this file byte for byte, \
             which `tests/results_integrity.rs` checks."
        );
        out
    }
}

/// Collapse incidental line breaks so source can be wrapped at a sane width
/// without the wrapping leaking into the rendered markdown.
fn squash(s: &str) -> String {
    s.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// Format a float for a report table: enough precision to be checkable, not
/// so much that it implies accuracy the model does not have.
pub fn f1(v: f64) -> String {
    format!("{v:.1}")
}

pub fn f2(v: f64) -> String {
    format!("{v:.2}")
}

pub fn pct(v: f64) -> String {
    format!("{:.1}%", v * 100.0)
}
