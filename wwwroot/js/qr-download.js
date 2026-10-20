async function renderQrCard(blob, params) {
  const source = URL.createObjectURL(blob);
  try {
    const img = new Image(); img.src = source; await img.decode();
    const canvas = document.createElement("canvas"); canvas.width = 620; canvas.height = 728;
    const ctx = canvas.getContext("2d");
    if (!ctx) throw new Error("Cannot create QR image.");
    ctx.fillStyle = "white"; ctx.fillRect(0, 0, 620, 728);
    ctx.imageSmoothingEnabled = false; ctx.drawImage(img, 130, 28, 360, 360);
    ctx.textBaseline = "middle";
    [["Ngân hàng", "bankName"], ["Số tài khoản", "accountNo"], ["Tên tài khoản", "accountName"], ["Số tiền", "amount"], ["Nội dung", "remark"]].forEach(([label, key], i) => {
      const y = 410 + i * 58;
      ctx.fillStyle = "#f5f8fc"; ctx.fillRect(28, y, 220, 58);
      ctx.strokeStyle = "#d2dce6"; ctx.strokeRect(28, y, 564, 58);
      ctx.beginPath(); ctx.moveTo(248, y); ctx.lineTo(248, y + 58); ctx.stroke();
      ctx.font = "bold 16px Arial"; ctx.fillStyle = "#566680"; ctx.fillText(label, 42, y + 29);
      ctx.font = "16px Arial"; ctx.fillStyle = "#142034";
      const text = params.get(key)?.trim() || "-";
      const words = text.split(/\s+/), lines = []; let line = "";
      for (const word of words) {
        const next = line ? line + " " + word : word;
        if (line && ctx.measureText(next).width > 316) { lines.push(line); line = word; } else line = next;
      }
      lines.push(line);
      lines.forEach((value, n) => ctx.fillText(value, 262, y + 29 + (n - (lines.length - 1) / 2) * 18, 316));
    });
    return await new Promise((resolve, reject) => canvas.toBlob(result => result ? resolve(result) : reject(new Error("Cannot create QR image.")), "image/png"));
  } finally { URL.revokeObjectURL(source); }
}
async function loadQrDownloadImage(url) {
  const source = url.searchParams.get("qrUrl");
  if (!source) throw new Error("Missing QR image URL.");
  // The user's browser may reach the provider even when the IIS host cannot.
  try {
    const direct = await fetch(source, {
      credentials: "omit", mode: "cors", signal: AbortSignal.timeout(10000)
    });
    if (direct.ok && direct.headers.get("content-type")?.toLowerCase().startsWith("image/")) {
      const blob = await direct.blob();
      if (blob.size) return blob;
    }
  } catch { /* Providers without CORS require the same-origin proxy below. */ }

  const rawUrl = new URL(url); rawUrl.searchParams.set("rawQr", "true");
  const response = await fetch(rawUrl, { credentials: "same-origin", signal: AbortSignal.timeout(30000) });
  if (response.redirected || response.status === 401 || response.status === 403)
    throw new Error("Cannot download QR image. Please sign in again or retry.");
  if (!response.ok) {
    const data = await response.json().catch(() => null);
    throw new Error((data?.message || "Cannot download QR image. Please retry.") + (data?.traceId ? ` (${data.traceId})` : ""));
  }
  const type = response.headers.get("content-type")?.toLowerCase() || "";
  if (!type.startsWith("image/") && !type.startsWith("application/octet-stream"))
    throw new Error("The server did not return a QR image. Please sign in again or retry.");
  return response.blob();
}
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
    const blob = await renderQrCard(await loadQrDownloadImage(url), url.searchParams);
    const objectUrl = URL.createObjectURL(blob), download = document.createElement("a");
    download.href = objectUrl;
    download.download = (link.getAttribute("download") || url.searchParams.get("downloadName") || "shipnet-9pay-qr.png").replace(/[^a-zA-Z0-9_.-]/g, "_");
    if (!download.download.toLowerCase().endsWith(".png")) download.download += ".png";
    document.body.append(download); download.click(); download.remove();
    setTimeout(() => URL.revokeObjectURL(objectUrl), 60000);
  } catch (error) { window.alert(error.message || "Cannot download QR image."); }
  finally { delete link.dataset.downloading; }
}, true);
