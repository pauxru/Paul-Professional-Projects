export interface FilterState {
  q: string;
  track: string;
  language: string;
}

export interface SearchProject {
  number: number;
  title: string;
  summary: string;
  track: string;
  languages: string[];
  focus: string[];
}

export const emptyFilters: FilterState = { q: "", track: "", language: "" };

export function normalizeSearch(value: string): string {
  return value.normalize("NFKD").replace(/\p{M}/gu, "").toLowerCase().trim();
}

export function projectSearchText(project: SearchProject): string {
  const aliases = project.languages.flatMap((language) => language === "C#" ? ["csharp", "dotnet", ".NET"] : []);
  return normalizeSearch([project.number, project.title, project.summary, project.track, ...project.languages, ...project.focus, ...aliases].join(" "));
}

export function matchesFilters(searchText: string, track: string, languages: string[], state: FilterState): boolean {
  if (state.track && state.track !== track) return false;
  if (state.language && !languages.includes(state.language)) return false;
  return normalizeSearch(state.q).split(/\s+/).filter(Boolean).every((term) => searchText.includes(term));
}

export function readFilters(params: URLSearchParams, tracks: string[], languages: string[]): { state: FilterState; invalid: string[] } {
  const state = {
    q: params.get("q") ?? "",
    track: params.get("track") ?? "",
    language: params.get("language") ?? "",
  };
  const invalid: string[] = [];
  if (state.track && !tracks.includes(state.track)) {
    state.track = "";
    invalid.push("track");
  }
  if (state.language && !languages.includes(state.language)) {
    state.language = "";
    invalid.push("language");
  }
  return { state, invalid };
}

export function updateFilterUrl(url: URL, state: FilterState): URL {
  const result = new URL(url);
  for (const key of ["q", "track", "language"] as const) {
    if (state[key].trim()) result.searchParams.set(key, state[key].trim());
    else result.searchParams.delete(key);
  }
  result.hash = "";
  return result;
}
