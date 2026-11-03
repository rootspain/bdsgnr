# What's new in Blast Designer

<!--
Format (read by the program and shown on the first start of a new version):
## <version> — <date>, ### <section>, "- " item; an indented line continues the item; **bold**, `code`.
Sections: New, Changed, Fixed. The Russian text is in CHANGELOG.md, with the same versions.
-->

## 0.2.2 — 2026-11-01

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
- Drawing tools showed an empty label box by the cursor before the first point.
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
