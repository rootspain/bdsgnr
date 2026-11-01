# What's new in Blast Designer

<!--
Format (read by the program and shown on the first start of a new version):
## <version> — <date>, ### <section>, "- " item; an indented line continues the item; **bold**, `code`.
Sections: New, Changed, Fixed. The Russian text is in CHANGELOG.md, with the same versions.
-->

## 0.1.8 — 2026-11-01

### New
- **Bench element recognition** from a mine survey: Survey ribbon ▸ Recognize (AI).
  Finds crests, toes, contours and blast outlines, and lists the benches with their height,
  face width and face angle. A click changes a segment's class; Apply sets the line types,
  splits the lines and builds the surface from the survey with all its lines as breaklines — one undo step.
- Recognition by a **trained model** (experimental) or by rules — a switch in the window.
  The model was trained on synthetic surveys; check the result, the dashed segments first.
- "Save labels…" in the bench elements window keeps the checked classes to improve the recognition;
  the file holds no coordinates.
- DXF / DWG import options window redesigned.
- DXF / DWG import: layers switched off in the drawing come in hidden or are skipped; lines at elevation 0
  next to the survey go to "(2D)" layers; object and layer colours, arcs and splines come in;
  pickets drawn as blocks take their elevation from an attribute.
- Usage statistics count switching tabs of the ribbon and the right-hand panel.

### Changed
- Visualization window: speeds 0.1x, 0.5x and 1x in the blast's real time, 1x by default.
- The "Time" button in the viewport and "Isochrones" on the Analysis tab are now "Visualization".
- Selecting an object with the Select tool brings the Properties tab of the right-hand panel forward.
- The arrow shape in the First movement window is now simply "Auto".

### Fixed
- Multi-line text from DXF / DWG came in with AutoCAD's formatting codes.
- Blocks with attributes in DXF / DWG were skipped on import.

## 0.1.7 — 2026-10-31

### New
- **What's new** window on the first start of a new version; open it again from Help ▸ What's new.
- **Usage statistics** in the settings: the most used buttons, menus and hot keys, flags switched on and
  straight back off, and the commands followed by a long pause.
- The timing line stays in the project and remembers its holes: moving or editing the line reprograms
  the delays of those holes.
- Timing line window redesigned: preview of the times on the plan, summary of intervals and the vibration window.
- Visibility tab: two columns and new labels — bearing, temperature, deck masses, deck sequence,
  detonator ID; dip and actual tracks, grade point, deviation, burden marker, MWD profile.
- First movement window redesigned; it opens with everything in view.

### Changed
- The hole number is shown in blue at the top right, the firing order at the top left.
- New projects show collars, charge columns, hole numbers and the firing order by default.
- The "Tracks" button is now "Depths"; "Charge 3D" shows the charge columns also without depth lines.
- Visualization window: the scrub bar shrinks with the window, speeds x1, x2 and x4.
- The Row tool ends the row at the second point; Shift+click adds bends.
- Opening the timing line with electronic detonators shows the hole order instead of the hole number.

### Fixed
- Timing lines from older files reprogram their holes when moved.
- First movement arrows no longer stack with another analysis on the plan.
