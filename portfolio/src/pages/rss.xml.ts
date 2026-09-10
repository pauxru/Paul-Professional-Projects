import rss from "@astrojs/rss";
import type { APIRoute } from "astro";
import { notes } from "../data/notes";
import { absoluteUrl, pathTo } from "../lib/urls";

export const GET: APIRoute = ({ site }) => {
  if (!site) throw new Error("A configured site URL is required for the RSS feed.");
  const sorted = [...notes].sort((a, b) => new Date(b.published).getTime() - new Date(a.published).getTime());
  return rss({
    title: "Paul Rukwaro \u2014 Engineering notes",
    description: "Engineering notes on distributed systems, retrieval evaluation and reliability, written from self-directed technical study alongside the project library.",
    site: absoluteUrl("/", site),
    items: sorted.map((note) => ({
      title: note.title,
      description: note.summary,
      link: pathTo(`/notes/${note.slug}/`),
      pubDate: new Date(note.published),
      categories: [note.topic],
    })),
    customData: "<language>en-us</language>",
  });
};
