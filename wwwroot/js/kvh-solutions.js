(() => {
  const selectedForm = document.getElementById("kvhSyncSelectedForm");
  const selectedInputs = document.getElementById("kvhSelectedInputs");
  const selectedButton = document.getElementById("kvhSyncSelectedButton");
  const selectedCount = document.getElementById("kvhSelectedCount");
  const selectAll = document.getElementById("kvhSelectAll");
  const boxes = Array.from(document.querySelectorAll(".kvh-device-checkbox"));

  function refreshSelected() {
    if (!selectedInputs || !selectedButton) return;
    selectedInputs.innerHTML = "";
    const checked = boxes.filter((box) => box.checked);
    checked.forEach((box, index) => {
      const input = document.createElement("input");
      input.type = "hidden";
      input.name = `DeviceIds[${index}]`;
      input.value = box.value;
      selectedInputs.appendChild(input);
    });
    selectedButton.disabled = checked.length === 0;
    if (selectedCount) selectedCount.textContent = String(checked.length);
    if (selectAll) {
      selectAll.indeterminate = checked.length > 0 && checked.length < boxes.length;
      selectAll.checked = boxes.length > 0 && checked.length === boxes.length;
    }
  }

  selectAll?.addEventListener("change", () => {
    boxes.forEach((box) => {
      box.checked = selectAll.checked;
    });
    refreshSelected();
  });
  boxes.forEach((box) => box.addEventListener("change", refreshSelected));
  selectedForm?.addEventListener("submit", refreshSelected);
  refreshSelected();

  document.querySelectorAll("form[data-kvh-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      const message = form.getAttribute("data-kvh-confirm") || "Bạn có chắc muốn thực hiện thao tác này?";
      if (!window.confirm(message)) {
        event.preventDefault();
        return;
      }

      const submitButton = form.querySelector("button[type='submit']");
      submitButton?.classList.add("is-processing");
      if (submitButton) submitButton.disabled = true;
    });
  });

  document.querySelectorAll("[data-copy-value]").forEach((button) => {
    button.addEventListener("click", async () => {
      const value = button.getAttribute("data-copy-value") ?? "";
      if (!value) return;
      await navigator.clipboard?.writeText(value);
      button.classList.add("is-copied");
      const usesIconCopy = button.classList.contains("table-copy-btn");
      const original = button.textContent;
      if (usesIconCopy) {
        button.dataset.copyTooltip = "Copied";
      } else {
        button.textContent = "Đã sao chép";
      }
      window.setTimeout(() => {
        button.classList.remove("is-copied");
        if (usesIconCopy) {
          button.dataset.copyTooltip = "Copy";
        } else {
          button.textContent = original;
        }
      }, 1200);
    });
  });

  const syncAllModal = document.querySelector("[data-kvh-sync-all-modal]");
  document.querySelectorAll("[data-kvh-open-sync-all]").forEach((button) => {
    button.addEventListener("click", () => {
      if (syncAllModal) syncAllModal.hidden = false;
    });
  });

  function closeModal(modal) {
    if (modal) modal.hidden = true;
  }

  document.querySelectorAll("[data-kvh-close-modal]").forEach((button) => {
    button.addEventListener("click", () => {
      closeModal(button.closest(".kvh-modal-backdrop"));
    });
  });

  document.querySelectorAll(".kvh-modal-backdrop").forEach((backdrop) => {
    backdrop.addEventListener("click", (event) => {
      if (event.target === backdrop) closeModal(backdrop);
    });
  });

  const jsonModal = document.querySelector("[data-kvh-json-modal]");
  const jsonTitle = document.getElementById("kvhJsonTitle");
  const jsonContent = document.getElementById("kvhJsonContent");
  function sanitizeJson(value) {
    if (Array.isArray(value)) return value.map((item) => sanitizeJson(item));
    if (!value || typeof value !== "object") return value;

    return Object.fromEntries(Object.entries(value).map(([key, entry]) => {
      const normalized = key.toLowerCase();
      if (normalized.includes("token") || normalized.includes("authorization") || normalized.includes("secret") || normalized.includes("password")) {
        return [key, "***"];
      }
      return [key, sanitizeJson(entry)];
    }));
  }

  document.querySelectorAll("[data-kvh-json]").forEach((button) => {
    button.addEventListener("click", () => {
      if (!jsonModal || !jsonContent) return;
      const title = button.getAttribute("data-kvh-json-title") || "Nội dung JSON";
      const raw = button.getAttribute("data-kvh-json") || "";
      if (jsonTitle) jsonTitle.textContent = title;
      try {
        jsonContent.textContent = JSON.stringify(sanitizeJson(JSON.parse(raw)), null, 2);
      } catch {
        jsonContent.textContent = raw;
      }
      jsonModal.hidden = false;
    });
  });

  document.querySelector("[data-kvh-copy-json]")?.addEventListener("click", async () => {
    const value = jsonContent?.textContent ?? "";
    if (value) await navigator.clipboard?.writeText(value);
  });

  const actionMenus = Array.from(document.querySelectorAll(".kvh-action-menu"));
  const actionPanels = new WeakMap();
  let activeActionMenu = null;

  function closeActionMenu(menu, restoreFocus = false) {
    const panel = actionPanels.get(menu) ?? menu.querySelector(".kvh-action-panel");
    const summary = menu.querySelector("summary");
    if (panel?.classList.contains("is-portaled")) {
      panel.classList.remove("is-portaled");
      panel.removeAttribute("style");
      menu.appendChild(panel);
    }
    menu.open = false;
    summary?.setAttribute("aria-expanded", "false");
    if (activeActionMenu === menu) activeActionMenu = null;
    if (restoreFocus) summary?.focus();
  }

  function positionActionMenu(menu) {
    const summary = menu.querySelector("summary");
    const panel = actionPanels.get(menu) ?? menu.querySelector(".kvh-action-panel");
    if (!summary || !panel || !menu.open) return;
    const rect = summary.getBoundingClientRect();
    if (!panel.classList.contains("is-portaled")) {
      document.body.appendChild(panel);
      panel.classList.add("is-portaled");
    }

    panel.style.position = "fixed";
    panel.style.visibility = "hidden";
    panel.style.left = "0px";
    panel.style.top = "0px";
    panel.style.width = "max-content";

    const panelRect = panel.getBoundingClientRect();
    const width = Math.max(180, panelRect.width || 180);
    const height = Math.max(40, panelRect.height || 40);
    const margin = 10;
    let left = rect.right - width;
    let top = rect.bottom + 8;

    if (left < margin) left = rect.left;
    if (left + width > window.innerWidth - margin) left = window.innerWidth - width - margin;
    if (left < margin) left = margin;
    if (top + height > window.innerHeight - margin) top = rect.top - height - 8;
    if (top < margin) top = margin;

    panel.style.position = "fixed";
    panel.style.top = `${top}px`;
    panel.style.left = `${left}px`;
    panel.style.width = `${width}px`;
    panel.style.visibility = "visible";
    summary.setAttribute("aria-expanded", "true");
  }

  actionMenus.forEach((menu) => {
    const panel = menu.querySelector(".kvh-action-panel");
    if (panel) actionPanels.set(menu, panel);
    menu.querySelector("summary")?.setAttribute("aria-haspopup", "menu");
    menu.addEventListener("toggle", () => {
      if (!menu.open) {
        closeActionMenu(menu);
        return;
      }
      actionMenus.forEach((other) => {
        if (other !== menu && other.open) closeActionMenu(other);
      });
      activeActionMenu = menu;
      positionActionMenu(menu);
    });
  });

  document.addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof Node)) return;
    actionMenus.forEach((menu) => {
      const panel = actionPanels.get(menu) ?? menu.querySelector(".kvh-action-panel");
      if (menu.contains(target) || panel?.contains(target)) return;
      if (menu.open) closeActionMenu(menu);
    });
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && activeActionMenu?.open) {
      event.preventDefault();
      closeActionMenu(activeActionMenu, true);
    }
  });

  window.addEventListener("resize", () => {
    if (activeActionMenu?.open) positionActionMenu(activeActionMenu);
  });
  window.addEventListener("scroll", () => {
    if (activeActionMenu?.open) positionActionMenu(activeActionMenu);
  }, true);

  const batchDetailModal = document.querySelector("[data-kvh-batch-detail-modal]");
  const batchDetailBody = batchDetailModal?.querySelector("[data-kvh-batch-items]");
  const batchDetailSearch = batchDetailModal?.querySelector("[data-kvh-batch-search]");
  const batchDetailCount = batchDetailModal?.querySelector("[data-kvh-batch-result-count]");
  const batchDetailSummary = batchDetailModal?.querySelector("#kvhBatchDetailSummary");
  let batchDetailRows = [];

  const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");

  const batchStatusText = (value) => ({
    SUCCESS: "Thành công",
    EMPTY: "Không có subscription",
    FAILED: "Thất bại",
    PROCESSING: "Đang xử lý",
    PENDING: "Đang chờ",
    RETRY_WAIT: "Chờ chạy lại",
    CANCELLED: "Đã hủy"
  }[String(value || "").toUpperCase()] || value || "-");

  const renderBatchRows = () => {
    if (!batchDetailBody) return;
    const keyword = (batchDetailSearch?.value || "").trim().toLocaleLowerCase("vi");
    const visibleRows = batchDetailRows.filter((item) => {
      const text = [
        item.deviceName, item.vesselName, item.kitNumber, item.terminalId,
        item.trafficId, item.status, item.errorCode, item.errorMessage
      ].join(" ").toLocaleLowerCase("vi");
      return !keyword || text.includes(keyword);
    });
    batchDetailBody.innerHTML = visibleRows.length
      ? visibleRows.map((item) => `
        <tr>
          <td>${escapeHtml(item.deviceName || `Thiết bị #${item.deviceId}`)}</td>
          <td>${escapeHtml(item.vesselName || "-")}</td>
          <td><strong>${escapeHtml(item.kitNumber || "-")}</strong><small>${escapeHtml(item.terminalId || "-")}</small></td>
          <td>${escapeHtml(item.trafficId || "-")}</td>
          <td><span class="subscription-status">${escapeHtml(batchStatusText(item.status))}</span></td>
          <td class="kvh-wrap-cell">${escapeHtml(item.errorMessage || item.errorCode || "-")}</td>
        </tr>`).join("")
      : '<tr><td colspan="6" class="tenant-empty">Không có thiết bị phù hợp.</td></tr>';
    if (batchDetailCount) batchDetailCount.textContent = `${visibleRows.length} / ${batchDetailRows.length} thiết bị`;
  };

  batchDetailSearch?.addEventListener("input", renderBatchRows);
  document.querySelectorAll("[data-kvh-batch-detail]").forEach((button) => {
    button.addEventListener("click", async () => {
      if (!batchDetailModal || !batchDetailBody) return;
      const id = button.getAttribute("data-kvh-batch-detail");
      if (!id) return;
      batchDetailModal.hidden = false;
      batchDetailSearch.value = "";
      batchDetailBody.innerHTML = '<tr><td colspan="6" class="tenant-empty">Đang tải danh sách thiết bị...</td></tr>';
      try {
        const response = await fetch(`/KvhSolutions/BatchStatus?id=${encodeURIComponent(id)}`, {
          headers: { Accept: "application/json" }
        });
        if (!response.ok) throw new Error("batch detail request failed");
        const batch = await response.json();
        batchDetailRows = Array.isArray(batch.items) ? batch.items : [];
        if (batchDetailSummary) {
          batchDetailSummary.textContent = `Batch #${batch.id}: ${batchDetailRows.length} thiết bị, ${batchStatusText(batch.status)}`;
        }
        renderBatchRows();
      } catch {
        batchDetailRows = [];
        batchDetailBody.innerHTML = '<tr><td colspan="6" class="tenant-empty">Không tải được chi tiết batch.</td></tr>';
      }
    });
  });

  const detailTabs = Array.from(document.querySelectorAll("[data-kvh-detail-tab]"));
  const detailPanels = Array.from(document.querySelectorAll(".kvh-detail-tab-panel"));
  detailTabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const targetId = tab.getAttribute("data-kvh-detail-tab");
      if (!targetId) return;
      detailTabs.forEach((item) => {
        const isActive = item === tab;
        item.classList.toggle("is-active", isActive);
        item.setAttribute("aria-selected", String(isActive));
      });
      detailPanels.forEach((panel) => {
        panel.hidden = panel.id !== targetId;
      });
    });
  });

  const runningRows = Array.from(document.querySelectorAll(".kvh-batch-row[data-kvh-running='true']"));
  if (runningRows.length === 0) return;

  const finalStatuses = new Set(["COMPLETED", "COMPLETED_WITH_ERRORS", "FAILED", "CANCELLED"]);
  const timers = runningRows.map((row) => {
    const id = row.getAttribute("data-batch-id");
    const timer = window.setInterval(async () => {
      if (document.hidden || !id) return;
      const response = await fetch(`/KvhSolutions/BatchStatus?id=${encodeURIComponent(id)}`, {
        headers: { Accept: "application/json" }
      });
      if (!response.ok) return;
      const batch = await response.json();
      row.querySelector(".kvh-batch-status").textContent = batch.status;
      row.querySelector(".kvh-batch-success").textContent = batch.successItems;
      row.querySelector(".kvh-batch-empty").textContent = batch.emptyItems;
      row.querySelector(".kvh-batch-failed").textContent = batch.failedItems;
      row.querySelector(".kvh-batch-progress").style.width = `${batch.progressPercent}%`;
      row.querySelector(".kvh-batch-progress-text").textContent = `${batch.processedItems} / ${batch.totalItems}`;
      if (finalStatuses.has(batch.status)) window.clearInterval(timer);
    }, 7000);
    return timer;
  });
  window.addEventListener("beforeunload", () => timers.forEach((timer) => window.clearInterval(timer)));
})();
