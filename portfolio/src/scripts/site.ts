const header = document.querySelector<HTMLElement>("[data-site-header]");
const toggle = document.querySelector<HTMLButtonElement>("[data-nav-toggle]");
const navigation = document.querySelector<HTMLElement>("#primary-navigation");
const menuLabel = document.querySelector<HTMLElement>("[data-menu-label]");

if (header && toggle && navigation && menuLabel) {
  header.dataset.enhanced = "true";
  toggle.hidden = false;

  const setOpen = (open: boolean, returnFocus = false) => {
    toggle.setAttribute("aria-expanded", String(open));
    header.dataset.menuOpen = String(open);
    menuLabel.textContent = open ? "Close" : "Menu";
    if (returnFocus) toggle.focus();
  };

  toggle.addEventListener("click", () => setOpen(toggle.getAttribute("aria-expanded") !== "true"));
  navigation.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest("a")) setOpen(false);
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && toggle.getAttribute("aria-expanded") === "true") {
      setOpen(false, true);
    }
  });
  document.addEventListener("pointerdown", (event) => {
    if (event.target instanceof Node && !header.contains(event.target)) setOpen(false);
  });
  header.addEventListener("focusout", (event) => {
    if (event.relatedTarget instanceof Node && !header.contains(event.relatedTarget)) setOpen(false);
  });
  window.matchMedia("(min-width: 1001px)").addEventListener("change", (event) => {
    if (event.matches) setOpen(false);
    else if (navigation.contains(document.activeElement)) setOpen(true);
  });
}

for (const block of document.querySelectorAll<HTMLElement>(".prose pre")) {
  const code = block.querySelector("code");
  if (!code || !navigator.clipboard) continue;
  const button = document.createElement("button");
  button.type = "button";
  button.className = "copy-code";
  button.textContent = "Copy";
  button.setAttribute("aria-label", "Copy code example");
  button.addEventListener("click", () => {
    navigator.clipboard.writeText(code.textContent ?? "").then(
      () => {
        button.textContent = "Copied";
        button.setAttribute("aria-label", "Code copied");
      },
      () => {
        button.textContent = "Select text to copy";
        button.setAttribute("aria-label", "Clipboard unavailable. Select the code and copy it manually.");
      },
    );
  });
  block.append(button);
}

export {};
