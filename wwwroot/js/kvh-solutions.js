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

  document.querySelectorAll("[data-copy-value]").forEach((button) => {
    button.addEventListener("click", async () => {
      const value = button.getAttribute("data-copy-value") ?? "";
      if (!value) return;
      await navigator.clipboard?.writeText(value);
      button.classList.add("is-copied");
      const original = button.textContent;
      button.textContent = "Copied";
      window.setTimeout(() => {
        button.classList.remove("is-copied");
        button.textContent = original;
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
  document.querySelectorAll("[data-kvh-json]").forEach((button) => {
    button.addEventListener("click", () => {
      if (!jsonModal || !jsonContent) return;
      const title = button.getAttribute("data-kvh-json-title") || "Payload JSON";
      const raw = button.getAttribute("data-kvh-json") || "";
      if (jsonTitle) jsonTitle.textContent = title;
      try {
        jsonContent.textContent = JSON.stringify(JSON.parse(raw), null, 2);
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
