// Saves bytes as a file in the browser (the incurred cost .xlsx workbook). Kept in a file for the Content-Security-Policy.
export function downloadBytes(fileName, contentType, bytes) {
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
