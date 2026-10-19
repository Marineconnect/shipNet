// Validate the response before saving: error pages must never become .txt downloads.
document.addEventListener("click", async event => {
  const link = event.target instanceof Element
    ? event.target.closest("#downloadNinePayQr, #downloadGroupedNinePayQr, [data-qr-download]") : null;
  if (!link) return;
  event.preventDefault();
  if (link.getAttribute("aria-disabled") === "true" || link.dataset.downloading === "true") return;
  const url = new URL(link.href, location.href);
  if (url.origin !== location.origin || url.pathname !== "/Payments/DownloadNinePayQrCard") return;
  link.dataset.downloading = "true";
  try {
    const response = await fetch(url, { credentials: "same-origin" });
    if (response.redirected || response.status === 401 || response.status === 403)
      throw new Error("Cannot download QR image. Please sign in again or retry.");
    if (!response.ok) {
      const data = await response.json().catch(() => null);
      throw new Error((data?.message || "Cannot download QR image. Please retry.") + (data?.traceId ? ` (${data.traceId})` : ""));
    }
    if (!response.headers.get("content-type")?.toLowerCase().startsWith("image/png"))
      throw new Error("Cannot download QR image. Please sign in again or retry.");
    const blob = await response.blob();
    const signature = new Uint8Array(await blob.slice(0, 8).arrayBuffer());
    if (![137,80,78,71,13,10,26,10].every((value, index) => signature[index] === value))
      throw new Error("Cannot download QR image. Please sign in again or retry.");
    const objectUrl = URL.createObjectURL(blob), download = document.createElement("a");
    download.href = objectUrl;
    download.download = (link.getAttribute("download") || url.searchParams.get("downloadName") || "shipnet-9pay-qr.png").replace(/[^a-zA-Z0-9_.-]/g, "_");
    if (!download.download.toLowerCase().endsWith(".png")) download.download += ".png";
    document.body.append(download); download.click(); download.remove();
    setTimeout(() => URL.revokeObjectURL(objectUrl), 60000);
  } catch (error) { window.alert(error.message || "Cannot download QR image."); }
  finally { delete link.dataset.downloading; }
}, true);
