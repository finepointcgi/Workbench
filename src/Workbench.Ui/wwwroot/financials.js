// Saves text as a file in the browser (CSV exports).
export function downloadText(fileName, contentType, text) {
    const url = URL.createObjectURL(new Blob([text], { type: contentType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
