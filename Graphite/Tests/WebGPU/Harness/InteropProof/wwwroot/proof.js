// The page's side of the proof app: lets the C# code report where it got to.
export function baseUrl() {
    return document.baseURI;
}

export function report(ok, summary, log) {
    const banner = document.getElementById('result');
    banner.textContent = (ok ? 'PASS  ' : 'FAIL  ') + summary;
    banner.className = ok ? 'pass' : 'fail';
    document.getElementById('log').textContent = log;
    window.__proof = { ok, summary, log };
}
