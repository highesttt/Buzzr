#include <math.h>
#include <string.h>
#include <windows.h>
#include <shobjidl.h>
#include <initguid.h>

static ITaskbarList3 *g_taskbar = NULL;

__declspec(dllexport) int __stdcall InitTaskbar(void) {
    if (g_taskbar) return 0;
    HRESULT hr = CoCreateInstance(
        &CLSID_TaskbarList, NULL, CLSCTX_INPROC_SERVER,
        &IID_ITaskbarList3, (void**)&g_taskbar);
    if (FAILED(hr)) return hr;
    hr = g_taskbar->lpVtbl->HrInit(g_taskbar);
    if (FAILED(hr)) { g_taskbar->lpVtbl->Release(g_taskbar); g_taskbar = NULL; return hr; }
    return 0;
}

__declspec(dllexport) int __stdcall SetOverlay(HWND hwnd, HICON hIcon) {
    if (!g_taskbar) {
        int r = InitTaskbar();
        if (r != 0) return r;
    }
    return g_taskbar->lpVtbl->SetOverlayIcon(g_taskbar, hwnd, hIcon, L"");
}

__declspec(dllexport) int __stdcall ClearOverlay(HWND hwnd) {
    if (!g_taskbar) return -1;
    return g_taskbar->lpVtbl->SetOverlayIcon(g_taskbar, hwnd, NULL, L"");
}

static void FillRoundedRect(DWORD *pixels, int imgW, int imgH,
    int rx, int ry, int rw, int rh, int cr,
    BYTE bgR, BYTE bgG, BYTE bgB)
{
    for (int y = ry; y < ry + rh; y++) {
        for (int x = rx; x < rx + rw; x++) {
            if (x < 0 || x >= imgW || y < 0 || y >= imgH) continue;
            double dx = 0, dy = 0;
            int corner = 0;
            if (x - rx < cr && y - ry < cr) {
                dx = cr - (x - rx) - 0.5; dy = cr - (y - ry) - 0.5; corner = 1;
            } else if (rx + rw - x <= cr && y - ry < cr) {
                dx = (x - (rx + rw - cr)) + 0.5; dy = cr - (y - ry) - 0.5; corner = 1;
            } else if (x - rx < cr && ry + rh - y <= cr) {
                dx = cr - (x - rx) - 0.5; dy = (y - (ry + rh - cr)) + 0.5; corner = 1;
            } else if (rx + rw - x <= cr && ry + rh - y <= cr) {
                dx = (x - (rx + rw - cr)) + 0.5; dy = (y - (ry + rh - cr)) + 0.5; corner = 1;
            }
            double alpha = 1.0;
            if (corner) {
                double dist = sqrt(dx*dx + dy*dy);
                if (dist > cr) continue;
                if (dist > cr - 1.0) alpha = cr - dist;
            }
            BYTE a = (BYTE)(255.0 * alpha);
            pixels[y * imgW + x] = ((DWORD)a << 24)
                | ((DWORD)(BYTE)(bgR * alpha) << 16)
                | ((DWORD)(BYTE)(bgG * alpha) << 8)
                | (BYTE)(bgB * alpha);
        }
    }
}

__declspec(dllexport) HICON __stdcall CreateBadgeIcon(
    int count, BYTE bgR, BYTE bgG, BYTE bgB, BYTE fgR, BYTE fgG, BYTE fgB)
{
    wchar_t text[8];
    if (count > 99) wcscpy(text, L"99+");
    else wsprintfW(text, L"%d", count);
    int len = (int)wcslen(text);

    /* Use 2x system icon size for crisp rendering, always square */
    int sysSize = GetSystemMetrics(SM_CXSMICON);
    if (sysSize < 16) sysSize = 16;
    int size = sysSize * 2;
    int canvasW = size;
    int canvasH = size;

    /* Font and padding */
    int fontSize = (int)(size * 0.66);
    int padX = (int)(size * 0.18 + 0.5);
    int padY = (int)(size * 0.06 + 0.5);

    /* First measure text to know how wide we need */
    HDC screenDC = GetDC(NULL);
    HDC hdc = CreateCompatibleDC(screenDC);
    ReleaseDC(NULL, screenDC);

    HFONT hFont = CreateFontW(
        -fontSize, 0, 0, 0,
        FW_MEDIUM, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS,
        L"Segoe UI");
    HFONT tmpFont = SelectObject(hdc, hFont);

    SIZE ts;
    GetTextExtentPoint32W(hdc, text, len, &ts);
    SelectObject(hdc, tmpFont);

    /* Badge sized to text */
    int badgeH = ts.cy + padY * 2;
    if (badgeH > canvasH) badgeH = canvasH;
    int badgeW = ts.cx + padX * 2;
    if (badgeW < badgeH) badgeW = badgeH;

    /* Clamp to canvas — don't expand, Windows shrinks wider icons */
    if (badgeW > canvasW) badgeW = canvasW;

    BITMAPINFO bmi = {0};
    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth = canvasW;
    bmi.bmiHeader.biHeight = -canvasH;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;

    void *bits = NULL;
    HBITMAP hBmp = CreateDIBSection(hdc, &bmi, DIB_RGB_COLORS, &bits, NULL, 0);
    if (!hBmp || !bits) { DeleteObject(hFont); DeleteDC(hdc); return NULL; }

    HBITMAP oldBmp = SelectObject(hdc, hBmp);
    memset(bits, 0, canvasW * canvasH * 4);
    DWORD *pixels = (DWORD*)bits;

    HFONT oldFont = SelectObject(hdc, hFont);

    /* Center in canvas */
    int badgeX = (canvasW - badgeW) / 2;
    int badgeY = (canvasH - badgeH) / 2;

    /* Clamp corner radius to half the smaller dimension — fully rounded */
    int cr = badgeH < badgeW ? badgeH / 2 : badgeW / 2;
    FillRoundedRect(pixels, canvasW, canvasH, badgeX, badgeY, badgeW, badgeH,
                    cr, bgR, bgG, bgB);

    /* Save alpha */
    int npx = canvasW * canvasH;
    BYTE *savedAlpha = (BYTE*)malloc(npx);
    for (int i = 0; i < npx; i++)
        savedAlpha[i] = (pixels[i] >> 24) & 0xFF;

    /* Draw text */
    SetTextColor(hdc, RGB(fgR, fgG, fgB));
    SetBkMode(hdc, TRANSPARENT);
    int nudge = -(int)(fontSize * 0.08 + 0.5); /* nudge text up to optically center */
    RECT rc = {badgeX, badgeY + nudge, badgeX + badgeW, badgeY + badgeH + nudge};
    DrawTextW(hdc, text, len, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

    /* Fix alpha */
    for (int i = 0; i < npx; i++) {
        DWORD px = pixels[i];
        BYTE cr2 = (px >> 16) & 0xFF;
        BYTE cg = (px >> 8) & 0xFF;
        BYTE cb = px & 0xFF;
        BYTE oa = savedAlpha[i];
        if (oa > 0) {
            pixels[i] = ((DWORD)oa << 24) | ((DWORD)cr2 << 16) | ((DWORD)cg << 8) | cb;
        } else if (cr2 || cg || cb) {
            pixels[i] = (0xFFu << 24) | ((DWORD)cr2 << 16) | ((DWORD)cg << 8) | cb;
        }
    }

    free(savedAlpha);
    SelectObject(hdc, oldFont);
    DeleteObject(hFont);
    SelectObject(hdc, oldBmp);

    ICONINFO ii = {0};
    ii.fIcon = TRUE;
    ii.hbmMask = hBmp;
    ii.hbmColor = hBmp;
    HICON hIcon = CreateIconIndirect(&ii);

    DeleteObject(hBmp);
    DeleteDC(hdc);
    return hIcon;
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    if (fdwReason == DLL_PROCESS_ATTACH) {
        CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    } else if (fdwReason == DLL_PROCESS_DETACH) {
        if (g_taskbar) { g_taskbar->lpVtbl->Release(g_taskbar); g_taskbar = NULL; }
        CoUninitialize();
    }
    return TRUE;
}
