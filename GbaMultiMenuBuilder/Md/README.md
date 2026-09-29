# ROM archive import

The desktop applications accept ZIP files through their existing main-window
drag-and-drop and file-import paths. Every recognized game member is listed;
there is no separate archive-import window and no first-member-only selection.

Supported game extensions include GBA/AGB, GB/GBC, NES, MD/GEN/SMD/BIN and 32X.
Common headers distinguish renamed binaries. Images, text notes, nested ZIPs
and macOS resource-fork entries are ignored. Duplicate member names retain
separate archive indices. A single-game archive uses its archive title; a
multi-game archive uses each member's title.

`RomArchiveImportService` provides shared discovery and application-owned caches
for existing GBA/GB/FC analyzers. Cache filenames are derived from source identity
and member index, never from an untrusted extraction path. `MdRomImportService`
handles raw, word-swapped and SMD-interleaved data without modifying the source.
Legacy Chinese ZIP filenames are decoded when UTF-8 decoding fails.

Import and hardware support are separate. Known unsupported MD boards remain
visible with a reason, and unknown headers require review. MD writing remains
disabled until its hardware backend is available. Games for another platform
are retained and identified rather than silently mixed into the selected
platform's build. Existing build-time cartridge and directory limits still apply;
in particular, the FC directory limit does not truncate ZIP discovery.
