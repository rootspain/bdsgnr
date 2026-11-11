# What's new in Blast Designer

<!--
Format (read by the program and shown on the first start of a new version):
## <version> — <date>, ### <section>, "- " item; an indented line continues the item; **bold**, `code`.
Sections: New, Changed, Fixed. The Russian text is in CHANGELOG.md, with the same versions.
-->

## 0.3.1 — 2026-11-06

### New
- **Line vertices.** The vertices of selected polylines and polygons show round grips. Dragging a grip moves the vertex,
  snapping to other objects; Esc cancels. The whole line still moves by the part between its vertices.
  Shift and a click on a selected line add a vertex, Ctrl and a click on a grip delete it.
- **Report preview window.** The report, the plan and the First movement sheet show on the screen before they are saved: pages,
  zoom and the choices on the left. From there a report goes to a PDF file or through the print dialog of Windows, macOS or Linux.
  The drawings of a report have no surface mesh. The text after the title at the top of the pages is a field with substitutions such as {name}. Sections have switches, and each can
  start on a new page. PgUp, PgDn and the mouse wheel turn the pages, Ctrl and the wheel zoom.
  The left panel is resized by dragging its edge, for each tab apart, and the program settings keep its width.
  The Reports and Plan commands and the Print button of the First movement window open it. Pages are built in the background and do not change the blast.
- **Report design.** A tab of the preview window: the frame of the sheet and stamps with their content, on all or given pages;
  stamps of one place adjoin each other horizontally or vertically,
  with fields such as {blast}, {date}, {page}.
  The design is kept for the next reports, and the Save design… and Load design… buttons work with a *.bdreport file holding the choices of the report and the plan, the frame,
  stamps and visibility. This tab replaces the
  Report designer window.
  Its Visibility section sets which kinds of object and whether the coordinate grid show on the drawings of the report. The boxes
  are kept in the program settings apart from the main window; the very first time the objects are taken from it.
  The Footer section switches on the date, with or without the time, and the page number on all, the first or given pages.
  A column of a stamp of width 0 does not show. The caption and the text of each line of a stamp have their own alignment:
  buttons on the line go through left, centre and right. Buttons on the line of the widths align a whole column vertically.
  The plan and each section have these settings of their own: the check boxes and the ring act on the drawing of the page, named in the heading. A page without a drawing has no such section. On the sheets of a section the grid shows the elevations. In the report each section has a page of its own. Beside them a ring of arrows and zoom buttons move and scale the drawing on the sheet. The lock in the middle of the ring
  holds the frame: the check boxes no longer change the scale and position of the drawing. With Shift the step of the arrows and the zoom is 4 times smaller.
- Tie panel: a Continue after creating check box, off by default. The entry of ties goes on from the hole just tied, as with
  Shift held; a click on that hole again or Esc finishes it.
- **Section lines.** The Drawing list has a Section line tool: one segment, the entry ends with the second click, of the type Cross section.
  The line is drawn thicker than a polyline. In the Section window arrows go to the previous and next line, the Print button opens the preview. The Properties tab of a line of that type has a Section along line button. A surface in a section and a horizontal slice is cut to the half width. A section goes along the first segment of the line. The colour of the line on the screen is set in the Viewport section of the program settings.
  The preview has a Section chip and sheet, one sheet per line, and the report a Cross section section after the Plan.
- **DeviTronic detonator** in the product catalogue: electronic, with its own family "DeviTronic". It also appears in projects
  from older files, and is offered wherever the Electronic detonator is: primers, loading rules, In-hole delay, BPD export.
- **Electronic primers with times of their own.** A primer with an electronic detonator has the field "Offset from the hole time, ms"
  in the hole loading window in place of the delay. The electronic settings have a new group "Decks and primers": inter deck delay,
  intra deck delay, deck order (bottom up, top down, simultaneous), primer order and "Use offsets". The explosive decks between
  inert ones fire in their order, the primers of a deck one after another by the intra deck delay; with offsets each primer fires
  at the hole's time plus its offset.
- **Loading rules keep every primer.** A rule saved from a hole's column keeps all the primers of a deck with their height, booster,
  detonator, delay and offset. Rules of older files are read as a list of like primers.
- **EBS opening point.** The Opening point button places a point the holes with an electronic detonator take their times from:
  along the burden and the spacing with their delays and the bearing of the burden, from the time of the first hole. With
  "Open the segment at this point" the times grow to both sides of the nearest hole, a V pattern. The window shows the overall
  relief, ms/m; the plan shows two rays marked like "42ms / 4 m". A hole belongs to one opening point or one front.
- **Burden relief over triangles.** The Burden relief command opens a window: the triangles between the holes in five colours,
  blue to red by their relief, ms/m, with the legend, an "Upper limit" field and the value under the cursor. The plan shows the
  same while the window is open. Reports have a new section "Burden relief". The burden relief dots at the holes are gone.
- **Signal down the tube.** A downhole shock tube detonator has the field "Signal down the tube", ms/m, in the catalogue: its primer
  fires later by depth × that value. 0 by default.
- **Font size of the hole labels.** The Visibility tab, Hole labels section, has a "Font size" field (whole numbers, with arrows,
  6–40, 12 by default). The size is kept in the project and in the program settings: a new blast starts with the last one chosen.

### Changed
- **The timing line is drawn as two segments.** A press places the vertex, where two segments meet at 30°;
  drag with the button held to set the direction and length, a click gives segments 60 px long. While the front is drawn or turned
  a ray leaves the vertex: the times of the holes grow that way. On a selected front, in the tool and in Select (V), the accent circle
  at the vertex turns it about its centre, the circles at the base change its angle and length, and dragging the line moves it. After
  every change the holes of the front are reprogrammed, in one undo step. Timing lines from earlier files open and time as before.
- The timing line keeps its English name; in Russian it is now «Фронт отбойки ЭСИ» throughout the program.
- First movement window: the Show on plan switch is off by default.
- The report preview always has the First movement chip, also when opened from the Reports command.
- **The EBS timing front programs only holes with an electronic detonator.** Pyrotechnic holes are left out and fire by their tie-up;
  their count shows in the front window and the status bar. Times an older version gave them are cleared when the file is opened
  (manual ones stay). Holes out of the front's reach, for a chevron those behind its vertex, lose their time; their numbers are listed after Apply.
- **Rounding of EBS times.** The electronic settings have an "Allow sub ms" check box in place of the time step: off, the times
  are rounded to 1 ms, on, to 0.1 ms, as in SHOTPlus. Files with a step under 1 ms open with it on.
- EBS timing front window: when some of its holes have a manual time, the note "There are holes with a manual time" and the
  check box "Apply to holes with a manual time" show. The check box applies to this time only.
- **The charges of a hole fire each at its own time.** Explosive decks between inert ones are separate charges; a charge fires with
  its earliest primer: electronic by the hole's time and the deck delays, pyrotechnic by the tie-up and the detonator's delay. The hole
  fires with its first charge. The time envelope, the charge vs time plot, vibration and the MIC take each charge apart, with its mass.
  The timing front and first movement take the middle of the first charge to fire, a hole without a primer its collar.
  The BPD export writes each detonator with the time of its primer.
- **The EBS timing front follows its holes.** When a hole of a front is moved, loaded again or its primer moved, the front reprograms
  its holes in the same undo step, without opening its window again. Manual times stay.
- The largest delay counts the primers with an offset: a hole is not programmed when its time with its last primer passes the
  largest electronic delay. The message names the primer offset of such a hole.
- **Electric detonators taken out of the built-in catalogue** (25 and 500 ms and their family). The Electric detonator kind and
  the Electric type stay for products of your own. An older project keeps a built-in electric detonator only where it is used.
- **Shift in the middle of a Select drag** no longer puts the objects back on their X and Y: the way made so far is kept and they go
  on along Z; letting go of Shift keeps the height.

## 0.2.6 — 2026-11-06

### New
- New Project properties window: blast properties, the coordinate system, design data, types, tolerances, vibration and flyrock
  constants, vibration events, fragmentation, electronic settings, costs, outcomes and the summary block are pages on the left.
  The older commands and buttons open their page, and changes on several pages are undone in one step.
- The list of recent files shows the date and time each file was opened, in the format of the system. The startup window now writes dates the same way.
  The tip of a row gives the size of the file, or says it was deleted or moved; the disk is asked in the background, the tip says
  "<calculating...>" until it answers, and a lost path does not hold the menu up.
- First movement window: a Hide geometry check box, on by default. The plan of the window keeps the holes with their labels,
  initiation points, ties with their times and the arrows. There is a Print button, with no action yet.

### Changed
- The edit windows and tables have the modern look: a title band, fields in groups with their units, a bottom bar with a hint
  and an Apply button. This covers blast properties, design data, outcomes, costs, electronic settings, vibration, tolerances,
  flyrock, the coordinate system, the summary block, layer and object properties, operation parameters, hole and material
  types, loading and initiation rules, text import, the report designer and attachments.
- Messages and small prompts for a value have the same look.
- The Visual style command is gone: the same fields are in the program settings.
- The triangulation is faster: 40,000 points take 0.3 s instead of 4.6 s, and the time grows almost linearly. The result is the same.
- File ribbon: the Settings group no longer has the Settings button; they open from the gear in the top bar.

### Fixed
- Esc closes windows that open with no field chosen, such as Blast properties: the key went to the plan of the main window.
- Drop-down menus no longer blend into the ribbon: they have their own ground, outline, rounded corners and item highlight.
  Recent files show the file name with the folder below it, cut in the middle; the tip gives the full path.
- The render thread no longer reads the project: a frame is recorded on the main thread and only played back. A frame cannot
  break off or mix two states while the project changes.
- The first frame of a large surface is no longer held up: on a survey of 90,000 points it took 17 s, now 0.2 s.
- Open analysis windows kept calculating on the earlier state of the project after an undo: Flyrock prediction,
  Fragmentation prediction, Exclusion zone calculator, Design tolerance check, Burden check view, Timing overlap,
  Time envelope, Time envelope overview, Volume overview, Scale law calculator, the count of holes in Visualization.
- Opening another project or closing the current one with the Fragmentation prediction or Dashboard window open gave an error.

## 0.2.5 — 2026-11-05

### Changed
- Available resources: the tabs go Products, Types, Kinds, Families, Groups; on the tabs of the tables the bottom of the
  window says where and what they are used for.
- Analysis ribbon: the Fragmentation group has a Resources button, which opens the Available resources window.
- A click on an object no longer solves the times and rebuilds the model panels: only what depends on the selection is updated.
- Changes and undo are faster in a project with a large surface: a mesh that did not change is not stored again on each step.
  Undo keeps meshes within 512 MB; beyond that the oldest steps go.
- Autosave no longer stops the work: the recovery file is made in the background.
- The vibration map is built about 10 times faster.
- Selecting a large surface is much quicker: its mesh is no longer copied for the Properties tab.
- Timing arrows on the plan are worked out when the holes change, not on every frame.

### Fixed
- After an operation that ended in an error, hole times and tie labels no longer vanish until the next change.
- An error in a command no longer closes the program with work not saved: it is shown in a message and written to the log.
- A failure while writing the settings or the catalogue file no longer damages the old file.
- Available resources: the Kind field of a product and a family is now called Product kind.

## 0.2.4 — 2026-11-04

### New
- **Tables in the Available resources window:** sections Products, Families, Kinds, Groups and Types, whose records
  can be added, edited, deleted and restored. Deletion is logical: a record is no longer offered for anything new,
  and what already uses it goes on showing it. Deleting a type, kind or family takes its children, with a warning.
  Records are kept in the project and in catalog.json next to the settings; old projects are converted when opened
  and get the standard products they lack. Older versions of the program cannot open the new files.
- **Sliders under the A and f fields** of the Fragmentation inputs window. The A slider steps along the rock type
  table from 0 to 18 (the row bounds and the whole numbers inside the rows), the f slider along the Protodyakonov
  category bounds from 0 to 20. Under each field is the rock type or category the value falls in, with its range and
  example rocks; a value between table rows is described as weaker than, stronger than or between them.
- The minimum top stemming (in hole diameters) can be changed right in the Stemming check line of the hole loading
  window. It is kept in the program settings and applies to every loading check and to the warnings on the plan
  (25 diameters by default).
- The items of the Visibility tab have tooltips: where the item shows, what it is for and which calculations use the
  same data.
- **Hole labels left and right of the collar.** On the Visibility tab the labels (number, length, diameter,
  elevation, angle, bearing, explosive mass, order, times, detonator ID and others) are laid out in two columns, Left
  and Right. A label moves to the other side or to another place in its column by dragging it, or with the up, down
  and arrow buttons shown when the pointer is on its row. On the plan the labels switched on stand in columns on each
  side of the collar in the same order. The layout is saved with the project; Default order brings back the usual one.
  The hole graphics (track, collar, charge track, deviation and so on) stay as separate switches under Holes.
- The Properties tab has a Layer list: it shows the layer of the selection (or "(mixed)"), and picking another layer
  moves the selection there in one undo step.

- **Size units of fragmentation:** a mm | cm switch on the Size distribution group of the Fragmentation inputs window,
  mm by default. The unit is kept in the project, the preset and the settings and applies to the fields, X20/X50/X80,
  the chart, the dashboard and the CSV export.
- **Minimum stemming in the project and the loading rule:** the value is saved in both; opening a file takes its value,
  Load by rule replaces the current one.
- **Tie tool:** grey lines beside the tie show the capture area. The Capture width box (key F) shows and hides them and
  is kept in the settings; the + and − keys change the pick radius by 1.
- **Visualization window:** the speed is set as the time the blast is shown in: chips Natural, 2, 5, 10, 15 s or a value
  of your own. The choice and the boxes reach the settings at once.
- **First movement window:** a One colour (yellow) or Gradient switch, with a legend of the colours.
- **Exceptions window:** search by hole number.

### Changed
- Properties tab for holes: the Edit loading button on top, charge cards, fields in folding groups, two to a row; the
  fields no longer run out of the panel.
- Ties attach to holes already tied, and their direction comes from the initiation point whichever way they were drawn,
  also at 0 ms and in a Shift entry. A click on a hole finishes the entry; with Shift it goes on.
- Initiation catalogue: the Subtype group is gone, a type shows all its products; the type УВТ is now НСИ in Russian.
- The Visualization window is more compact: round Play and Stop buttons, a thin title, a fixed height.
- First movement window: arrows start at 1.2 m, the Auto chip is gone.
- The group titles of the fragmentation window are blue.
- Analysis ribbon: Quality control stands before Volumes, Fragmentation before Environment; the Recent button of the
  File ribbon has a new Russian name.
- Number fields of the Properties tab and of dialogs take their value on Enter, and the times follow at once.
- Select these holes and Highlight in the Exceptions window work on the rows chosen in the table, or on all the rows
  shown when none is chosen.
- The tables of the analysis windows sort numbers by value: 1, 2, 10, not 1, 10, 2.
- First movement says so when all holes fire at the same time, instead of saying that no hole is initiated.
- The last row of the Available resources list no longer hides under the buttons.
- **A and f are matched by tables, not by the Cunningham formula.** A by the rock type table: soft 0.6–2, medium
  strength 3–7, strong 8–12, extremely strong 13–18. f by the 15 Protodyakonov categories (X–I, f 0.3–20). A value typed
  in sets the other field by its share of its table's range, rounded to a whole number. When the value is at least the
  minimum of its table and the other would round to 0, the other field gets the minimum of its table. A value below
  the minimum of its table gives 0 in the other field.
- An A set in the fragmentation window is used in the prediction as it is, 0 too. In files of earlier versions A = 0
  is still calculated from the rock mass description.
- "Outside the usual range" in the fragmentation window follows the table ranges (A 0.6–18, f 0.3–20); the warning
  and the field are highlighted in red.
- Esc closes any dialog or tool window as Cancel (it presses the window's Cancel button when there is one). Windows
  without an input field (Quantities, Dashboard) take the focus themselves, so Esc closes them instead of reaching the
  plan.
- The theme preview in the program settings changes the plan background at once; the plan redraws when the theme or
  the accent colour changes.
- With Collar switched off, selected holes are drawn without a collar too, and holes are not snapped to.
- The stage row of the ribbon is lower.
- View everything (Home) fits only what is shown: objects on hidden layers, objects hidden by hand and those switched
  off by display switches and hole filters do not count.
- The hole legend on the plan is not shown when the holes shown are of one type or none.
- The fragmentation window no longer shows the "σc ≈ … MPa · HF = …" line under the f field.
