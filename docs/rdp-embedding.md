# RDP Embedding Design

Why the MstscAx tab is a top-level window pinned over the panel rect, and not a true child of the WinUI 3 visual tree.

## TL;DR

WinUI 3 has no first-class way to host a Win32 child HWND visibly inside a XAML element. Every approach we tried lost to the WinUI compositor's "airspace": even with `SetParent`, `WS_CHILD`, `WS_VISIBLE`, transparent backgrounds, and `WS_CLIPCHILDREN` on the parent, the XAML rendering surface paints over the child HWND and the tab looks empty.

Our shipping approach for `MstscAx` mode: the form hosting `MsRdpClient` is a **borderless top-level window owned by the WinUI window** (`GWLP_HWNDPARENT`), positioned in screen coordinates over `RdpHostPanel`'s rect by a 50 ms poll in `RdpSessionView`. It looks and behaves like an embedded panel; functionally it is a separate-but-pinned window.

## What we tried, and why each failed

### 1. `SetParent` to make the form a real `WS_CHILD`

The classical Win32 embedding pattern. We:
- Stripped `WS_OVERLAPPEDWINDOW`, set `WS_CHILD | WS_VISIBLE`.
- `SetParent(form, mainHwnd)`.
- `SetWindowPos` to position inside the panel rect.

Result: form HWND is correctly a child of the WinUI HWND (verified by `EnumChildWindows`), but visually invisible. The WinUI compositor draws on top regardless of z-order.

### 2. Reparent into `Microsoft.UI.Content.DesktopChildSiteBridge`

Instead of the top-level WinUI HWND, reparent into the bridge child window — the actual rendering surface. Same outcome: the bridge composes XAML on top of any child HWNDs.

### 3. `WS_CLIPCHILDREN` on the WinUI HWND

The classic WPF airspace mitigation. With `WS_CLIPCHILDREN`, traditional Win32 painting clips out child rectangles. WinUI 3 doesn't participate in classic Win32 painting — it composes via DirectComposition above HWND z-order. No effect.

### 4. `HWND_TOP` z-order + `SWP_SHOWWINDOW`

Re-stacking the child HWND to the top of its sibling list and forcing visible. The compositor still wins; child HWNDs cannot rise above WinUI's composition layer.

### 5. Top-level form pinned over the panel rect (current)

Stop fighting airspace. The form is a normal top-level window:

- **Owned** via `SetWindowLongPtr(GWLP_HWNDPARENT, mainHwnd)` so it follows owner z-order, hides on owner minimise, dies with owner, no taskbar entry.
- **Positioned** in screen coordinates: `RdpSessionView` computes the panel's screen rect from `TransformToVisual(rootContent)` + DPI scale (`GetDpiForWindow / 96`) + `ClientToScreen`. A `DispatcherQueueTimer` ticks every 50 ms; if bounds changed, calls `SetWindowPos(form, ownerHwnd, x, y, w, h, SWP_NOACTIVATE)`. `hWndInsertAfter = ownerHwnd` re-stacks the form just above the WinUI window each tick, so clicking back into XAML doesn't bury it.
- **Tab switch** is handled via `EffectiveViewportChanged` on `RdpHostPanel`: when the host tab is no longer visible the viewport collapses to 0×0 → we `ShowWindow(SW_HIDE)` the form so it doesn't sit on top of the wrong tab. When the tab comes back the viewport returns → `SW_SHOWNOACTIVATE` and a forced bounds re-emit.
- **Z-order recovery on click** — `Form.Deactivate` immediately calls `SetWindowPos(form, ownerHwnd, …, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` so the form snaps back above its owner without waiting for the next 50 ms poll.

## The "real" embedding paths we evaluated and rejected

### Microsoft.UI.Composition.Private.SystemVisualProxyVisualPrivate

The technique described in [imbushuo/DCompAdventure](https://github.com/imbushuo/DCompAdventure): walk through `ISystemVisualProxyVisualPrivateStatics` → `ISystemVisualProxyVisualPrivateInterop.GetHandle` → `IPartner.OpenShardTargetFromHandle` → `IDCompositionDevice::CreateSurfaceFromHwnd` → wrap as a `Microsoft.UI.Composition.Visual` and `SetElementChildVisual`. This works for a regular GDI/D3D HWND.

We didn't ship this because:

- The reference C# port targets **CsWinRT 1.6.3**, whose `lib` only contains `net5.0`. Our host is `net9.0-windows`.
- CsWinRT 2.x rewrote the projection model. The `[DynamicInterfaceCastableImplementation]` ABI helpers in the projection rely on 1.x source-generator output that 2.x doesn't produce. Cast errors like `Cannot convert WinRT.SingleInterfaceOptimizedObject to ISystemVisualProxyVisualPrivate`.
- Porting `DCompPrivateProjection` to 2.x means rewriting all the `ABI/*.cs` files using the new vtable model — multi-day work, no published reference.
- The interfaces involved are explicitly `Microsoft.UI.Composition.Private.*` — Microsoft can rename or remove them in any Windows App SDK update.
- Even if all that worked, mstscax draws via OLE inplace activation; whether `CreateSurfaceFromHwnd` captures OLE-rendered content reliably is an open question.

### `BitBlt` / `PrintWindow` capture into a `WriteableBitmap`

We prototyped this. Ran the form offscreen at `(-32000, -32000)` with `Opacity = 0`, used `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` at 30 fps to capture into a BGRA byte buffer, pushed bytes into a `WriteableBitmap` and called `Invalidate()`.

Read-only — input would have to be synthesised via `PostMessage(WM_MOUSEMOVE, …)` etc. Frame rate capped at the timer interval, every frame round-trips through GDI even when nothing changed, and `PrintWindow` doesn't capture the OS cursor.

The single-window approach gives us native rendering, full frame rate, real input, real cursor, real IME, real D3D for free — at the cost of accepting that the "embedded" thing is technically a separate window.

### FreeRDP

Replace mstscax entirely. Provides real frame callbacks; we'd composite into a `SwapChainPanel` via D2D/D3D and synthesise input. Truly in-tab, no airspace problem, and full quality.

Cost: new C library dependency, custom interop, rewrite of the auth UI (no built-in credential prompt), TLS / NLA on us. Multi-week project. Reserved for a future Phase 3 if mstscax's limitations bite.

## References

- [microsoft/microsoft-ui-xaml#4670 — ActiveX support: closed "not planned"](https://github.com/microsoft/microsoft-ui-xaml/issues/4670)
- [microsoft/microsoft-ui-xaml#9912 — embedding a custom Win32 window](https://github.com/microsoft/microsoft-ui-xaml/discussions/9912)
- [microsoft/microsoft-ui-xaml#7767 — `IDCompositionTarget` QI on a WinUI Visual returns `E_NOINTERFACE`](https://github.com/microsoft/microsoft-ui-xaml/issues/7767)
- [Mitigating airspace issues in WPF (Dwayne Need)](https://dwayneneed.github.io/wpf/2013/02/26/mitigating-airspace-issues-in-wpf-applications.html) — historical context; the airspace problem traces back to WPF.
- [HWND hosting on the XAML composition visual tree (imbushuo)](https://imbushuo.net/blog/archives/1010) — the only published working technique, via undocumented private APIs.
- [Can WinUI 3 host Win32 controls? — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1115740/can-we-use-both-winui3-controls-and-win32-controls)

## Code map

- `src/NexusRDM.RdpAx/MstscAxRdpSession.cs` — owned-top-level form, `Resize` reposition, `SetVisible` hide/show, `Deactivate` z-order re-assert.
- `src/NexusRDM/Views/RdpSessionView.xaml.cs` — DPI-scaled `GetPanelScreenBounds`, 50 ms `DispatcherQueueTimer` poll, `EffectiveViewportChanged` for tab-switch hide/show.
- `src/NexusRDM.Core/Interfaces/IRdpHandler.cs` — `Connect`, `Resize`, `SetVisible`, `BringToFront`, `SendCtrlAltDel`. The `MstscRdpSession` (separate-process backend) no-ops the visibility/foreground methods.
