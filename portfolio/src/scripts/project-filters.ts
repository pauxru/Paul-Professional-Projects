import { emptyFilters, matchesFilters, readFilters, updateFilterUrl, type FilterState } from "../lib/filters";

const form = document.querySelector<HTMLFormElement>("[data-project-filters]");
const query = document.querySelector<HTMLInputElement>("#project-search");
const track = document.querySelector<HTMLSelectElement>("#track-filter");
const language = document.querySelector<HTMLSelectElement>("#language-filter");
const results = document.querySelector<HTMLElement>("[data-results-count]");
const empty = document.querySelector<HTMLElement>("[data-empty-state]");
const warning = document.querySelector<HTMLElement>("[data-filter-warning]");
const cards = Array.from(document.querySelectorAll<HTMLElement>("[data-project-number]"));
const groups = Array.from(document.querySelectorAll<HTMLElement>("[data-track-group]"));

if (!form || !query || !track || !language || !results || !empty || !warning || cards.length === 0) {
  throw new Error("The project library is missing its required filter controls or content.");
}

const trackValues = Array.from(track.options, (option) => option.value);
const languageValues = Array.from(language.options, (option) => option.value);
const records = cards.map((card) => {
  const languages: unknown = JSON.parse(card.dataset.languages ?? "null");
  if (!Array.isArray(languages) || !languages.every((value): value is string => typeof value === "string")) {
    throw new Error(`Project ${card.dataset.projectNumber} has invalid language metadata.`);
  }
  if (!card.dataset.search || !card.dataset.track) {
    throw new Error(`Project ${card.dataset.projectNumber} has incomplete search metadata.`);
  }
  return { card, languages, search: card.dataset.search, track: card.dataset.track };
});

const applyFilters = (state: FilterState, persist: boolean) => {
  let count = 0;
  for (const record of records) {
    const visible = matchesFilters(record.search, record.track, record.languages, state);
    record.card.hidden = !visible;
    if (visible) count++;
  }
  for (const group of groups) {
    const visible = group.querySelectorAll<HTMLElement>("[data-project-number]:not([hidden])").length;
    group.hidden = visible === 0;
    const jump = document.querySelector<HTMLAnchorElement>(`[data-track-jump="${group.id}"]`);
    if (jump) jump.hidden = visible === 0;
    const countLabel = group.querySelector<HTMLElement>("[data-track-count]");
    if (countLabel) countLabel.textContent = `${visible} ${visible === 1 ? "project" : "projects"}`;
  }
  results.textContent = `${count} of ${cards.length} projects`;
  empty.hidden = count !== 0;
  if (persist) window.history.replaceState(null, "", updateFilterUrl(new URL(window.location.href), state));
};

const restoreFromUrl = () => {
  const { state, invalid } = readFilters(new URLSearchParams(window.location.search), trackValues, languageValues);
  query.value = state.q;
  track.value = state.track;
  language.value = state.language;
  warning.hidden = invalid.length === 0;
  warning.textContent = invalid.length ? `An unrecognized ${invalid.join(" or ")} filter was removed. The other filters still apply.` : "";
  applyFilters(state, invalid.length > 0);
};

const applyControls = () => {
  warning.hidden = true;
  applyFilters({ q: query.value, track: track.value, language: language.value }, true);
};

form.hidden = false;
restoreFromUrl();
form.addEventListener("input", applyControls);
form.addEventListener("change", applyControls);
form.addEventListener("submit", (event) => {
  event.preventDefault();
  applyControls();
});
for (const button of document.querySelectorAll<HTMLButtonElement>("[data-clear-filters]")) {
  button.addEventListener("click", () => {
    query.value = "";
    track.value = "";
    language.value = "";
    warning.hidden = true;
    applyFilters(emptyFilters, true);
    query.focus();
  });
}
window.addEventListener("popstate", restoreFromUrl);
