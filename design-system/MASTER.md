# Fraylo — Design System Master File (UI/UX Pro Max)

> **LOGIC:** When designing or implementing a specific view, first check `design-system/pages/[page-name].md`.
> If that file exists, its rules **override** this Master file.
> If not, strictly follow the rules below.

---

**Project:** Fraylo Desktop Studio  
**Tech Stack:** React 19 + TypeScript + Vite + Tailwind CSS 4 + shadcn/ui + Windows WPF/WebView2  
**Design Category:** Creative AI Video Automation / Desktop Studio  
**Aesthetic Style:** Warm near-black (`#110d0c`) with coral accent (`#ff5a45`) + warm ivory Light Mode (`#faf6f3`). Nguồn sự thật của token: `src/Fraylo.Web/src/styles/globals.css`<br>
**Accessibility Target:** WCAG AA+ / AAA (Minimum 4.5:1 contrast, keyboard trap & visible focus)

---

## 1. Global Color Tokens & Theming

Theme selection is persistent and system-synchronized across host WPF and frontend React:
- **Dark Mode (Default & Studio Visual Target):** Warm near-black background with subtle surface elevation and coral accents.
- **Light Mode:** Warm ivory background with deep coral accents and crisp borders.

| Semantic Token | Dark Mode (`[data-theme="dark"]`) | Light Mode (`[data-theme="light"]`) | Usage |
|---|---|---|---|
| `--color-background` | `#110d0c` | `#faf6f3` | Main window background |
| `--color-foreground` | `#f6efec` | `#1f1412` | Primary text |
| `--color-card` | `#1a1413` | `#ffffff` | Content cards, dialog surfaces |
| `--color-card-foreground` | `#f6efec` | `#1f1412` | Text on cards |
| `--color-popover` | `#1a1413` | `#ffffff` | Menus, dropdowns, tooltips |
| `--color-popover-foreground` | `#f6efec` | `#1f1412` | Text in popovers |
| `--color-primary` | `#ff5a45` | `#d63a26` | Primary brand accent & active states |
| `--color-primary-foreground` | `#1a0805` | `#ffffff` | Text on primary buttons/badges |
| `--color-secondary` | `#241b1a` | `#f6efeb` | Secondary button/badge background |
| `--color-secondary-foreground` | `#d9ccc8` | `#54423d` | Secondary text |
| `--color-muted` | `#241b1a` | `#f3ebe7` | Muted backgrounds, chips, table alt |
| `--color-muted-foreground` | `#a89994` | `#6b5a55` | Subtitles, placeholders, captions |
| `--color-accent` | `#31211f` | `#fdeae6` | Hover highlights on list items |
| `--color-accent-foreground` | `#f6efec` | `#1f1412` | Text on hover items |
| `--color-destructive` | `#f05252` | `#c81e1e` | Error states, delete actions |
| `--color-destructive-foreground` | `#ffffff` | `#ffffff` | Text on destructive buttons |
| `--color-border` | `#392b29` | `#e6d9d3` | Borders on cards, tables, dividers |
| `--color-input` | `#46352f` | `#dccdc6` | Input borders |
| `--color-ring` | `#ff8a78` | `#d63a26` | Focus rings (2px outline) |
| `--color-sidebar` | `#0b0807` | `#ffffff` | Fixed left navigation sidebar |
| `--color-sidebar-foreground` | `#b9aaa5` | `#54423d` | Sidebar icons and labels |
| `--color-sidebar-border` | `#261c1a` | `#eee3de` | Sidebar vertical separator |

---

## 2. Typography & Hierarchy

- **Font Family:** `Plus Jakarta Sans Variable` (tự host qua `@fontsource-variable/plus-jakarta-sans`, không gọi CDN), `-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`
- **Typographic Scale:**
  - `H1 / Page Header`: `text-2xl font-bold tracking-tight text-foreground`
  - `H2 / Section Title`: `text-base font-bold text-foreground`
  - `H3 / Card Header`: `text-sm font-semibold text-foreground`
  - `Body / Standard`: `text-xs font-normal text-foreground leading-normal`
  - `Caption / Muted`: `text-xs text-muted-foreground leading-relaxed`
  - `Micro / Badge`: `text-[10px] font-semibold uppercase tracking-wider`

---

## 3. Spacing & Radius System

- **Base Radius:** `0.5rem` (`rounded-lg` = 8px)
  - Small elements (badges, buttons, inputs): `rounded-md` (6px)
  - Cards, panels, dialogs: `rounded-xl` (12px)
  - Full-pill tags: `rounded-full`
- **Standard Spacing Grid:**
  - Compact padding / inline: `p-2` (8px), `p-2.5` (10px)
  - Card inner padding: `p-5` (20px) or `p-6` (24px)
  - Page container padding: `p-6` (24px)
  - Gap between cards: `gap-4` (16px) or `gap-5` (20px)

---

## 4. Component Standards (shadcn/ui + Tailwind v4)

### 4.1 Buttons
- **Variants:**
  - `default`: `bg-primary text-primary-foreground hover:bg-primary/90 shadow-sm cursor-pointer`
  - `outline`: `border border-input bg-background hover:bg-accent hover:text-accent-foreground cursor-pointer`
  - `secondary`: `bg-secondary text-secondary-foreground hover:bg-secondary/80 cursor-pointer`
  - `ghost`: `hover:bg-accent hover:text-accent-foreground text-muted-foreground cursor-pointer`
  - `destructive`: `bg-destructive text-destructive-foreground hover:bg-destructive/90 cursor-pointer`
- **Rule:** Never use custom un-styled HTML buttons without focus rings and transitions.

### 4.2 Cards
- **Base Style:** `rounded-xl border border-border bg-card text-card-foreground shadow-xs transition-colors hover:border-primary/50`
- **Structure:** `CardHeader` -> `CardContent` -> `CardFooter` (with `border-t border-border flex items-center justify-between`)
- **Rule:** No layout-shifting scale hovers (`scale-105`). Use border color transition (`hover:border-primary/50`).

### 4.3 Badges
- **Variants:**
  - `default`: Primary coral background
  - `secondary`: Subdued background for counts/tags
  - `success`: `bg-emerald-500/15 border-emerald-500/30 text-emerald-600 dark:text-emerald-400`
  - `warning`: `bg-amber-500/15 border-amber-500/30 text-amber-600 dark:text-amber-400`
  - `info`: `bg-sky-500/15 border-sky-500/30 text-sky-600 dark:text-sky-400`
  - `destructive`: Red background for errors

### 4.4 Icons
- **Strict Rule:** **Zero emojis as UI icons** (e.g. no 🎬, 🚀, ⚙️, ✨, ⚠️).
- Always use SVG icons from `lucide-react` (or `components/Icons.tsx`).
- Consistent sizing: `size-3.5` (14px) for inline badges/buttons, `size-4` (16px) for standard buttons/nav, `size-5` (20px) for section headers.

---

## 5. Anti-Patterns (Strictly Forbidden)

- ❌ **No Emojis as UI Icons:** Use Lucide SVG icons instead.
- ❌ **No Missing `cursor-pointer`:** All clickable cards, buttons, rows, tabs, switches must show pointer cursor.
- ❌ **No Layout-Shifting Hovers:** Never use `transform: scale(1.02)` that shifts sibling elements. Use border/opacity/shadow transitions.
- ❌ **No Hardcoded Hex Colors in Components:** Never use `bg-[#...]` or `text-[#...]`. Always use semantic classes (`bg-card`, `text-foreground`, `border-border`, etc.).
- ❌ **No Insufficient Contrast in Light Mode:** Slate-400 text on white is unreadable. Body text must be slate-900 (`#0F172A`) or slate-600 (`#475569`) minimum.
- ❌ **No Instant State Changes:** Always use smooth transitions (`transition-colors duration-200`).
- ❌ **No Invisible Focus:** Focus states must have `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2`.

---

## 6. Pre-Delivery Checklist

Before delivering any UI work, verify:
- [ ] No emojis used as icons (SVG only)
- [ ] All clickable elements have `cursor-pointer`
- [ ] Hover states use smooth transitions (150-300ms) with no layout shift
- [ ] Light & Dark mode contrast verified (minimum 4.5:1 ratio)
- [ ] Keyboard navigation and visible focus rings verified
- [ ] `pnpm build` succeeds with 0 errors
- [ ] All DOM tests pass 100%
