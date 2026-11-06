# What's new in Blast Designer

<!--
Format (read by the program and shown on the first start of a new version):
## <version> — <date>, ### <section>, "- " item; an indented line continues the item; **bold**, `code`.
Sections: New, Changed, Fixed. The Russian text is in CHANGELOG.md, with the same versions.
-->

## 0.2.5 — 2026-11-05

### Changed
- Available resources: the tabs go Products, Types, Kinds, Families, Groups; on the tabs of the tables the bottom of the
  window says where and what they are used for.
- Analysis ribbon: the Fragmentation group has a Resources button, which opens the Available resources window.
- The triangulation is faster: 40,000 points take 0.3 s instead of 4.6 s, and the time grows almost linearly. The result is the same.
- The edit windows and tables have the modern look: a title band, fields in groups with their units, a bottom bar with a hint
  and an Apply button. This covers blast properties, design data, outcomes, costs, electronic settings, vibration, tolerances,
  flyrock, the coordinate system, the summary block, layer and object properties, operation parameters, hole and material
  types, loading and initiation rules, text import, the report designer and attachments.
- Messages and small prompts for a value have the same look.
- The Visual style command is gone: the same fields are in the program settings.
- The list of recent files shows the date and time each file was opened, in the format of the system. The startup window now writes dates the same way.
  The tip of a row gives the size of the file, or says it was deleted or moved; the disk is asked in the background, the tip says
  "<calculating...>" until it answers, and a lost path does not hold the menu up.
- New Project properties window: blast properties, the coordinate system, design data, types, tolerances, vibration and flyrock
  constants, vibration events, fragmentation, electronic settings, costs, outcomes and the summary block are pages on the left.
  The older commands and buttons open their page, and changes on several pages are undone in one step.
- A click on an object no longer solves the times and rebuilds the model panels: only what depends on the selection is updated.
- Changes and undo are faster in a project with a large surface: a mesh that did not change is not stored again on each step.
  Undo keeps meshes within 512 MB; beyond that the oldest steps go.
- Autosave no longer stops the work: the recovery file is made in the background.
- The vibration map is built about 10 times faster.
- Selecting a large surface is much quicker: its mesh is no longer copied for the Properties tab.
- Timing arrows on the plan are worked out when the holes change, not on every frame.

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

## 0.2.3 — 2026-11-03

### New
- **The Fragmentation inputs window is redone**: cards of fields and a live X50, X80, oversize and fines
  prediction for the blast. The rock factor has two fields: A by the international classification (Cunningham) and
  f by Protodyakonov; f sets the strength σc = 10·f MPa and A is calculated from it and the rock mass (powdery,
  jointed, massive), and an A typed in recalculates f; the fields follow each other as they are typed. When f
  cannot give the A typed in with this rock mass, A is kept as typed. The fields take f from 0 to 50 and
  A from 0 to 25; outside the usual range (f 0.3–40,
  A 0.8–22) the field is highlighted. The inputs can be saved to a preset (.bdfrag file) and loaded
  into another blast.
- The Visualization window remembers the speed, the isochrone interval and the Isochrones and Colour by time
  switches: they apply at the next start and in other blasts.

### Changed
- **Initiation catalogue** replaces the tie palette: chosen down Type (shock tube, cord delay, detonating cord,
  electric, harness wire, EBS detonators, user defined) → Subtype (product kind) → Family → Variant (delay), with
  the current choice on top. Products in Available resources get a Family field (without it the family comes from
  the name). Tie delays follow the type: detonating cord by its length and VOD, electric wiring 0 ms (the delay is in
  the hole's detonator). New kinds Cord relay, Electric detonator and Harness wire; new projects get examples. The
  Ties button opens the catalogue, as does placing an initiation point; its position is remembered.
- Drawing button on the left rail: the tools are listed with icons; the triangle on the right or holding the left
  button opens the list, a click
  on the icon starts the tool picked last, which the button shows (and key W starts). The active tool's icon on the
  rail is in the accent colour.
- Drawing a polyline, polygon or other geometry finishes with a click on its last point.
- The initiation point star is larger and drawn over the hole, with a see-through circle in the middle that shows the
  hole under it; a click on the star's rays picks the point, a click in the middle the hole.
- An initiation point is placed on a hole and bound to it: it initiates that hole and the ties go on from the hole
  (placing it opens the Tie tool with a chain from it; a click on the star starts a tie from the hole as well). The
  hole counts as initiated in every check. Moving the hole moves the point, moving the point moves the hole; deleting
  the hole deletes the point, deleting the point leaves the hole. In projects of earlier versions a point standing on a
  collar is bound to that hole on opening.
- The first-movement arrows are recalculated when an initiation point or its hole is moved, also live while it is
  dragged (on the plan and in the First movement window); a bound point and its hole are dragged together.
- The Tie legend switch is gone from the Visibility tab.
- Placing an initiation point on a hole turns the existing ties so the signal runs away from it: each tie points from
  the hole the signal reaches first to the other (one undo step).
- The First movement button on the Analysis tab is called Rock movement.
- Holes ringed by Highlight (Exceptions, depth checks, duplicates and the other check windows) are cleared by Clear
  analysis on the viewport or Esc, like the other analysis displays.
- A row can be taken by its line (with rows shown): a selected row is dragged by the line between its holes, a click
  on the line of an unselected row selects its holes.
- Tie tool: with "Tie chain" on its panel (on by default) a line from hole to hole ties every hole on its way in turn
  (within the pick radius); without it, one tie from the first hole to the last. A second click on the last hole
  finishes the entry; no tie goes to a hole the signal already reaches (a tie comes in or an
  initiation point stands on it); a hole whose ties only leave it takes one.
- Pattern tool: blast direction, edge offset, number prefix and start number are on its panel; the Pattern
  properties window no longer opens after the outline is drawn. Tool panel fields stay on one line; the panel
  widens up to the view and the fields scroll beyond that.

### Fixed
- Drawing tools showed an empty label box by the cursor before the first point.

## 0.2.2 — 2026-11-01

### New
- **Recognized bench elements in layers of their own.** Apply in the Bench elements window puts crests, toes,
  contours and block outlines in layers in the class colours, so each type is shown or hidden in the layers panel.
  Setting a line's type by hand in Properties moves it the same way.
- **Surface wireframe setting**: Program settings ▸ Viewport ▸ Surface wireframe — Always, Hide while rotating
  or moving (the default), Only when selected or Never.
- **Surface kind Face.** "Build the surface from the survey" in the Bench elements window puts the faces between
  the recognized crests and toes into a surface of their own, "Faces", of this kind, and the platforms between the
  faces into "Benches" of kind Bench; the colours are in Program settings ▸ Viewport ▸ Surface colours.
- Frame rate in the corner of the plan: Program settings ▸ Viewport ▸ FPS, off by default.
- **Add holes** in the properties of a timing line: the holes with an electronic detonator that are on no timing line
  yet join this line, are programmed from it and selected. "Select assigned" there selects the holes the line fires.
- An OK button in the start assistant opens the file selected in the list.
- In the program settings a click on a colour swatch opens a colour picker: spectrum, hue, hex.

### Changed
- **The plan and the 3D view are several times faster**, especially a blast over a mine survey and moving
  the mouse over a large project.
- The Survey ribbon tab and the Survey layer group are now called "Topography".
- In the layers panel the Topography group comes before Drawing, layers without a group are at the bottom.
- Settings window: Apply applies and saves the settings and keeps the window open; the new OK button applies and
  closes. Buttons in order: Apply, Cancel, OK.
- The program no longer picks the discrete GPU itself: on laptops the integrated one is usually faster.
  The GPU can be chosen in Windows Settings ▸ Display ▸ Graphics.

### Fixed
- A surface built from a survey no longer throws triangles across crests, toes and other survey lines: the lines go
  into the triangulation as fixed edges. "Create surface" on the ribbon and in the selection menu does this too;
  the old way is kept as "Create surface without breaklines". Surfaces built before need to be built again.
- Timing contours stopped short of a row farther from its neighbour than three design burdens or spacings: the
  distance between the rows on the ground now counts.
- An occasional drawing error while using tools ("Collection was modified").

## 0.2.1 — 2026-11-01

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
