(() => {
  const instances = new WeakMap();
  const openMenus = new Map();
  let nextId = 0;

  const cleanText = value => String(value || "").replace(/\s+/g, " ").trim();

  function labelFor(select) {
    const explicit = select.getAttribute("aria-label") || select.title;
    if (explicit) return cleanText(explicit);
    const labels = Array.from(select.labels || []);
    const text = labels.map(label => {
      const clone = label.cloneNode(true);
      clone.querySelectorAll("select, input, button").forEach(control => control.remove());
      return cleanText(clone.textContent);
    }).filter(Boolean).join(" ");
    return text || "选择一项";
  }

  function enhance(select) {
    if (!(select instanceof HTMLSelectElement) || select.multiple || select.size > 1 || instances.has(select)) return;

    const wrapper = document.createElement("span");
    wrapper.className = `nte-dropdown ${select.className || ""}`.trim();
    wrapper.dataset.dropdownId = String(++nextId);
    select.parentNode.insertBefore(wrapper, select);
    wrapper.appendChild(select);
    select.classList.add("nte-dropdown__native");
    select.tabIndex = -1;
    select.setAttribute("aria-hidden", "true");

    const label = labelFor(select);
    const trigger = document.createElement("button");
    trigger.type = "button";
    trigger.className = "nte-dropdown__trigger";
    trigger.setAttribute("role", "combobox");
    trigger.setAttribute("aria-haspopup", "listbox");
    trigger.setAttribute("aria-expanded", "false");
    trigger.setAttribute("aria-label", label);
    trigger.id = `nte-dropdown-trigger-${wrapper.dataset.dropdownId}`;

    const valueText = document.createElement("span");
    valueText.className = "nte-dropdown__value";
    const chevron = document.createElement("span");
    chevron.className = "nte-dropdown__chevron";
    chevron.setAttribute("aria-hidden", "true");
    trigger.append(valueText, chevron);

    const menu = document.createElement("div");
    menu.className = "nte-dropdown__menu";
    menu.id = `nte-dropdown-menu-${wrapper.dataset.dropdownId}`;
    menu.setAttribute("role", "listbox");
    menu.setAttribute("aria-label", label);
    menu.hidden = true;
    trigger.setAttribute("aria-controls", menu.id);
    document.body.appendChild(trigger);
    wrapper.appendChild(trigger);
    // Portal the menu out of clipped cards and compact overlay panels.
    document.body.appendChild(menu);

    let options = [];
    let activeIndex = -1;
    let typeBuffer = "";
    let typeTimer = 0;
    let resizeObserver;

    const enabledIndices = () => options.map((option, index) => option.disabled ? -1 : index).filter(index => index >= 0);
    const selectedIndex = () => select.selectedIndex;
    const syncState = () => {
      options = Array.from(select.options).map((option, index) => ({
        value: option.value,
        text: cleanText(option.textContent),
        disabled: option.disabled || option.parentElement?.disabled === true,
        selected: index === selectedIndex(),
        hidden: option.hidden
      }));
      const selected = select.selectedOptions[0];
      valueText.textContent = selected ? cleanText(selected.textContent) : "";
      trigger.disabled = select.disabled;
      trigger.setAttribute("aria-disabled", String(select.disabled));
      trigger.setAttribute("aria-required", String(select.required));
      if (select.dataset.validationAttempted === "true") trigger.setAttribute("aria-invalid", String(!select.validity.valid));
      menu.replaceChildren();
      options.forEach((option, index) => {
        if (option.hidden) return;
        const row = document.createElement("div");
        row.className = "nte-dropdown__option";
        row.id = `${menu.id}-option-${index}`;
        row.setAttribute("role", "option");
        row.setAttribute("aria-selected", String(option.selected));
        row.setAttribute("aria-disabled", String(option.disabled));
        row.dataset.index = String(index);
        if (option.disabled) row.classList.add("is-disabled");
        if (option.selected) row.classList.add("is-selected");
        const text = document.createElement("span");
        text.className = "nte-dropdown__option-text";
        text.textContent = option.text;
        row.appendChild(text);
        if (option.selected) {
          const check = document.createElement("span");
          check.className = "nte-dropdown__check";
          check.setAttribute("aria-hidden", "true");
          check.textContent = "✓";
          row.appendChild(check);
        }
        row.addEventListener("pointerenter", () => setActive(index, false));
        row.addEventListener("pointerdown", event => event.preventDefault());
        row.addEventListener("click", () => choose(index));
        menu.appendChild(row);
      });
      if (menu.hidden) activeIndex = -1;
      else setActive(enabledIndices().includes(activeIndex) ? activeIndex : selectedIndex(), false);
    };

    const placeMenu = () => {
      const rect = trigger.getBoundingClientRect();
      const gutter = 8;
      const below = Math.max(0, window.innerHeight - rect.bottom - gutter);
      const above = Math.max(0, rect.top - gutter);
      const openDown = below >= Math.min(190, above) || below >= above;
      const available = Math.max(72, (openDown ? below : above) - 4);
      menu.style.left = `${Math.max(gutter, Math.min(rect.left, window.innerWidth - rect.width - gutter))}px`;
      menu.style.width = `${rect.width}px`;
      menu.style.maxHeight = `${Math.min(280, available)}px`;
      menu.style.top = openDown ? `${rect.bottom + 5}px` : "auto";
      menu.style.bottom = openDown ? "auto" : `${Math.max(gutter, window.innerHeight - rect.top + 5)}px`;
      menu.dataset.placement = openDown ? "bottom" : "top";
    };

    function setActive(index, scroll = true) {
      const enabled = enabledIndices();
      if (!enabled.length) {
        activeIndex = -1;
        trigger.removeAttribute("aria-activedescendant");
        return;
      }
      activeIndex = enabled.includes(index) ? index : enabled[0];
      menu.querySelectorAll(".nte-dropdown__option").forEach(row => row.classList.toggle("is-active", Number(row.dataset.index) === activeIndex));
      trigger.setAttribute("aria-activedescendant", `${menu.id}-option-${activeIndex}`);
      if (scroll) menu.querySelector(`#${CSS.escape(`${menu.id}-option-${activeIndex}`)}`)?.scrollIntoView({ block: "nearest" });
    }

    function open(preferLast = false) {
      if (select.disabled || !options.length) return;
      closeAll(select);
      menu.hidden = false;
      trigger.setAttribute("aria-expanded", "true");
      trigger.classList.add("is-open");
      openMenus.set(menu, close);
      placeMenu();
      const enabled = enabledIndices();
      const selected = selectedIndex();
      setActive(preferLast ? enabled[enabled.length - 1] : (enabled.includes(selected) ? selected : enabled[0]));
      if (!resizeObserver && "ResizeObserver" in window) {
        resizeObserver = new ResizeObserver(placeMenu);
        resizeObserver.observe(trigger);
      }
      window.addEventListener("resize", placeMenu);
      window.addEventListener("scroll", placeMenu, true);
    }

    function close(restoreFocus = false) {
      if (menu.hidden) return;
      menu.hidden = true;
      trigger.setAttribute("aria-expanded", "false");
      trigger.removeAttribute("aria-activedescendant");
      trigger.classList.remove("is-open");
      openMenus.delete(menu);
      activeIndex = -1;
      window.removeEventListener("resize", placeMenu);
      window.removeEventListener("scroll", placeMenu, true);
      if (restoreFocus) trigger.focus();
    }

    function choose(index) {
      const option = select.options[index];
      if (!option || option.disabled || option.parentElement?.disabled) return;
      select.value = option.value;
      select.dispatchEvent(new Event("input", { bubbles: true }));
      select.dispatchEvent(new Event("change", { bubbles: true }));
      close(true);
    }

    function moveActive(delta) {
      const enabled = enabledIndices();
      if (!enabled.length) return;
      const at = enabled.indexOf(activeIndex);
      const next = at < 0 ? (delta > 0 ? 0 : enabled.length - 1) : (at + delta + enabled.length) % enabled.length;
      setActive(enabled[next]);
    }

    function closeAll(except) {
      for (const [openMenu, closeMenu] of openMenus) {
        if (openMenu !== menu) closeMenu();
      }
    }

    trigger.addEventListener("click", () => {
      if (menu.hidden) open(); else close();
    });
    trigger.addEventListener("keydown", event => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        if (menu.hidden) open(event.key === "ArrowUp"); else moveActive(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Home" || event.key === "End") {
        event.preventDefault();
        if (menu.hidden) {
          open(event.key === "End");
          if (event.key === "Home") setActive(enabledIndices()[0]);
        }
        else {
          const enabled = enabledIndices();
          setActive(event.key === "Home" ? enabled[0] : enabled[enabled.length - 1]);
        }
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        if (menu.hidden) open(); else if (activeIndex >= 0) choose(activeIndex);
      } else if (event.key === "Escape" && !menu.hidden) {
        event.preventDefault();
        close(true);
      } else if (event.key === "Tab") close();
      else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
        typeBuffer += event.key.toLocaleLowerCase();
        clearTimeout(typeTimer);
        typeTimer = setTimeout(() => { typeBuffer = ""; }, 650);
        const match = enabledIndices().find(index => options[index].text.toLocaleLowerCase().startsWith(typeBuffer));
        if (match !== undefined) {
          if (menu.hidden) open();
          setActive(match);
        }
      }
    });

    select.addEventListener("change", syncState);
    select.addEventListener("input", syncState);
    select.addEventListener("invalid", () => {
      select.dataset.validationAttempted = "true";
      trigger.setAttribute("aria-invalid", "true");
      trigger.focus();
      open();
    });
    select.form?.addEventListener("reset", () => queueMicrotask(syncState));
    for (const property of ["value", "selectedIndex"]) {
      const descriptor = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, property);
      if (!descriptor?.get || !descriptor?.set) continue;
      try {
        Object.defineProperty(select, property, {
          configurable: true,
          get() { return descriptor.get.call(this); },
          set(value) { descriptor.set.call(this, value); syncState(); }
        });
      } catch { /* Older WebView versions still sync through change and mutation events. */ }
    }
    const observer = new MutationObserver(syncState);
    observer.observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled", "hidden", "label", "selected", "value"] });

    const instance = { menu, close, sync: syncState };
    instances.set(select, instance);
    wrapper.addEventListener("click", event => {
      if (event.target === wrapper) trigger.focus();
    });
    select.labels && Array.from(select.labels).forEach(labelElement => {
      labelElement.addEventListener("click", event => {
        if (event.target === trigger || trigger.contains(event.target)) {
          event.preventDefault();
          return;
        }
        if (!wrapper.contains(event.target)) {
          event.preventDefault();
          trigger.focus();
        }
      });
    });
    syncState();
  }

  document.addEventListener("pointerdown", event => {
    document.querySelectorAll(".nte-dropdown__menu:not([hidden])").forEach(menu => {
      if (menu.contains(event.target)) return;
      const triggerId = menu.id.replace("nte-dropdown-menu-", "nte-dropdown-trigger-");
      if (document.getElementById(triggerId)?.contains(event.target)) return;
      openMenus.get(menu)?.();
    });
  });

  const enhanceAll = root => root.querySelectorAll("select").forEach(enhance);
  window.NteDropdownMenus = {
    refresh(select) { if (select instanceof HTMLSelectElement) instances.get(select)?.sync(); },
    enhance(select) { enhance(select); },
    refreshAll() { enhanceAll(document); document.querySelectorAll(".nte-dropdown select").forEach(select => instances.get(select)?.sync()); }
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => enhanceAll(document));
  else enhanceAll(document);
})();
