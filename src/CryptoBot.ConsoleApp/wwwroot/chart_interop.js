// S26 T2：ApexCharts Blazor interop。
// 維護一個 elementId → ApexCharts 實例的 Map —— 重複 render 時先 destroy 舊的，避免葉片堆疊。
// 只暴露 render / dispose 兩個 API，Blazor 端透過 IJSRuntime 呼叫。
(function () {
    const instances = new Map();

    function toSeriesData(equityCurve) {
        // equityCurve: [{ timeUtc: ISO, equity: number }] —— 後端序列 PascalCase 降成 camelCase（System.Text.Json 預設）。
        // ApexCharts datetime 軸要 epoch ms。
        if (!Array.isArray(equityCurve)) return [];
        return equityCurve.map(p => {
            const t = new Date(p.timeUtc).getTime();
            const e = Number(p.equity);
            return [t, e];
        }).filter(pt => Number.isFinite(pt[0]) && Number.isFinite(pt[1]));
    }

    function toAnnotations(fills) {
        // fills: [{ createdAt: ISO, positionSide: 'Long'/'Short', side: 'Buy'/'Sell', averageFillPrice: { value }, quantity: { value } }]
        // 只用時間軸 annotation（xaxis），避免 y 軸尺度問題。
        if (!Array.isArray(fills)) return { xaxis: [] };
        const xaxis = fills.map(f => {
            const t = new Date(f.createdAt).getTime();
            if (!Number.isFinite(t)) return null;
            // 做多開/平 → 綠；做空開/平 → 紅。實際語意（開 vs 平）在 label 中標出。
            const isLong = f.positionSide === 'Long' || f.positionSide === 0;
            const color = isLong ? '#1de982' : '#ff4d6d';
            const sideTxt = (typeof f.side === 'string') ? f.side : (f.side === 0 ? 'Buy' : 'Sell');
            const price = f.averageFillPrice?.value ?? f.averageFillPrice ?? '—';
            return {
                x: t,
                strokeDashArray: 2,
                borderColor: color,
                label: {
                    borderColor: color,
                    orientation: 'horizontal',
                    style: { color: '#fff', background: color, fontSize: '10px' },
                    text: `${sideTxt} @ ${price}`,
                },
            };
        }).filter(a => a !== null);
        return { xaxis };
    }

    function buildOptions(series, annotations, title) {
        return {
            chart: {
                type: 'area',
                height: 360,
                background: 'transparent',
                foreColor: '#b9c0cc',
                toolbar: { show: true, tools: { download: false, zoom: true, pan: true, reset: true } },
                animations: { enabled: false },
            },
            series: [{ name: 'Equity (USDT)', data: series }],
            theme: { mode: 'dark' },
            stroke: { curve: 'smooth', width: 2, colors: ['#e8c468'] },
            fill: {
                type: 'gradient',
                gradient: { shadeIntensity: 0.6, opacityFrom: 0.35, opacityTo: 0.05, stops: [0, 100] },
                colors: ['#e8c468'],
            },
            xaxis: { type: 'datetime', labels: { datetimeUTC: true } },
            yaxis: { labels: { formatter: v => Number(v).toFixed(2) } },
            tooltip: { theme: 'dark', x: { format: 'yyyy-MM-dd HH:mm' } },
            grid: { borderColor: 'rgba(255,255,255,0.08)' },
            annotations,
            title: { text: title || '', style: { color: '#e8c468', fontSize: '13px' } },
            noData: { text: '無權益曲線資料', style: { color: '#b9c0cc' } },
        };
    }

    function ensureApex() {
        // CDN 若還沒載入 —— 例如首次 render 時尚未 ready — 延遲 100ms 重試 1 次。
        if (typeof window.ApexCharts !== 'undefined') return Promise.resolve(window.ApexCharts);
        return new Promise((resolve, reject) => {
            let tries = 20;
            const t = setInterval(() => {
                if (typeof window.ApexCharts !== 'undefined') {
                    clearInterval(t);
                    resolve(window.ApexCharts);
                } else if (--tries <= 0) {
                    clearInterval(t);
                    reject(new Error('ApexCharts CDN not loaded within 2s.'));
                }
            }, 100);
        });
    }

    async function render(elementId, equityCurve, fills, title) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const Apex = await ensureApex();
        const series = toSeriesData(equityCurve);
        const annotations = toAnnotations(fills);
        const options = buildOptions(series, annotations, title);

        // 既有實例 → 先 destroy 再重建，避免 ApexCharts 內部記憶體洩漏。
        if (instances.has(elementId)) {
            try { instances.get(elementId).destroy(); } catch { /* ignore */ }
            instances.delete(elementId);
        }
        const chart = new Apex(el, options);
        instances.set(elementId, chart);
        await chart.render();
    }

    function dispose(elementId) {
        if (!instances.has(elementId)) return;
        try { instances.get(elementId).destroy(); } catch { /* ignore */ }
        instances.delete(elementId);
    }

    window.cryptoBotChart = { render, dispose };
})();

// S39：非 HTTPS / 舊瀏覽器下 navigator.clipboard 無法使用 —— 提供一個 execCommand 後備。
// 使用者端的回傳值：true = 已寫入剪貼簿；false = 連 fallback 也失敗（此時由 UI 顯示原文給手動複製）。
window.cryptoBotClipboard = {
    copy(text) {
        try {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return !!ok;
        } catch {
            return false;
        }
    }
};
