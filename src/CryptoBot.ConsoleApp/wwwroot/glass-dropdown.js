// S56 T2 真·根治：GlassDropdown 元件用的 click-outside 偵測器。
// Blazor 元件在「點 trigger 展開」後呼叫 glassDropdown.register(id, dotnetRef)，
// 我們在 document 層掛一個 mousedown listener：若點擊目標不在 host (data-dropdown-id) 內，
// 就呼叫 .NET 端 CloseFromOutside，由 Blazor 把 _open 設回 false 並 re-render。
//
// 為何用 mousedown 而非 click：mousedown 比 click 早觸發，避免 React/Blazor 在 click
// 處理到一半時還來不及拒絕新開啟的其他 dropdown，造成兩個同時開著。
//
// 為何用 setTimeout(0)：打開 dropdown 的 click event 本身會往 document 冒泡（stopPropagation
// 只影響 Blazor 的事件路由，不影響原生 DOM event），若 listener 同一拍已掛上，
// 會立刻觸發 CloseFromOutside 關掉自己。延遲一拍掛確保這次 click 過去了再開始監聽。
window.glassDropdown = {
    register: function (id, dotnetRef) {
        const host = document.querySelector(`[data-dropdown-id="${id}"]`);
        if (!host) {
            return { dispose: function () { } };
        }

        const handler = function (ev) {
            if (!host.contains(ev.target)) {
                dotnetRef.invokeMethodAsync('CloseFromOutside').catch(function () { /* 元件已 dispose */ });
            }
        };

        const timer = setTimeout(function () {
            document.addEventListener('mousedown', handler);
        }, 0);

        return {
            dispose: function () {
                clearTimeout(timer);
                document.removeEventListener('mousedown', handler);
            }
        };
    }
};
