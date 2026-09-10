import type { EngineeringNote } from "../data/types";

const dateFormatter = new Intl.DateTimeFormat("en-US", {
  year: "numeric",
  month: "long",
  day: "numeric",
  timeZone: "UTC",
});

export function formatDate(value: string): string {
  return dateFormatter.format(new Date(value));
}

export function readingMinutes(note: Pick<EngineeringNote, "sections">): number {
  const words = note.sections.reduce(
    (total, section) => total + section.paragraphs.join(" ").trim().split(/\s+/).filter(Boolean).length,
    0,
  );
  return Math.max(1, Math.ceil(words / 200));
}
