# Rescue Robot Features

## Product vision

Help rescuers search areas affected by earthquakes and other natural disasters using affordable robots, a custom 3D-printed visor, and a glove controller. Robots search autonomously, while a rescuer can take over a robot to inspect a survivor, communicate with them, and mark their location. A Nemotron agent coordinates the fleet and prioritizes cases so one rescuer can supervise multiple robots while attending to other duties.

This document captures the intended experience and feature scope. Features listed here are planned, not claims of completed implementation. See [the implementation plan](docs/plan.md) for architecture, interfaces, priorities, and hardware details.

## Rescuer equipment and controls

- **Custom 3D-printed visor:** Display a live feed from the selected robot, with survivor and hazard information overlaid.
- **Head-tracked camera:** Turning or tilting the rescuer's head controls the robot's camera pan and tilt.
- **Glove controller with joystick:** Provide direct movement and turning controls. Forward, backward, left, and right control robot motion; the intended meaning of up/down input needs to be defined for the walking robot, such as camera tilt or body height.
- **Visor front-camera view:** Let the rescuer see their own surroundings through a camera facing forward from the visor when moving around the incident site.
- **Voice commands:** Allow the rescuer to give fleet instructions to the Nemotron agent without continuously operating the joystick.

## Robot perception and search

- **Autonomous search:** Explore assigned areas and report potential survivors without requiring continuous manual control.
- **SLAM mapping and localization:** Build a map and estimate the robot's position to support navigation, coverage tracking, and survivor location marking.
- **Survivor detection:** Use camera-based detection and available infrared or thermal sensing to identify potential survivors. SLAM supports mapping and localization; survivor detection is a separate perception task.
- **Environmental sensing:** Use infrared, light, distance, and other available sensors to help identify obstacles, drops, and hazardous conditions. Exact hazard coverage depends on the sensors installed.
- **Manual takeover:** Allow a rescuer to select a robot and directly inspect a detected survivor through its live feed.

## Survivor assistance

- **Communication through the robot:** Let the rescuer comfort the survivor and explain that help is coming. A robot speaker plays the rescuer's voice; a robot microphone captures the survivor's response.
- **Beacon deployment:** Drop a beacon near the survivor to mark their location for responders.
- **Survivor location record:** Associate each detection with a mapped location and case so responders can find the survivor and track follow-up.

## Nemotron coordination and fleet supervision

- **Autonomous fleet operation:** Send multiple robots into an impacted area and coordinate their search assignments.
- **Voice-directed agent:** Translate the rescuer's spoken instructions into fleet coordination actions.
- **Survivor queue:** Maintain a shared queue of detected survivors and requests for operator attention.
- **Urgency ranking:** Use available observations to flag potentially critical cases and prioritize human review and response. Agent assessments are provisional and should be reviewable by a rescuer.
- **Operator handoff:** Support switching from fleet supervision to direct control of a selected robot, then returning it to autonomous search.
- **Shared operational view:** Show robot status, searched areas, survivor locations, queue order, and agent actions.

## Intended rescue workflow

1. Deploy robots to search assigned areas of the disaster site.
2. The rescuer wears the visor and glove controller while supervising the fleet through voice commands.
3. A robot detects a potential survivor, records their location, and creates a case for review.
4. The agent ranks the case in the survivor queue using the available observations.
5. The rescuer takes over the robot, views its feed, moves the camera with head motion, and drives with the joystick.
6. The rescuer communicates with the survivor through the robot and deploys a beacon if available.
7. Responders use the location record and beacon to reach the survivor, while robots continue searching other areas.

## Relationship to the implementation plan

The existing plan defines the initial core loop as autonomous search, person detection, manual handoff, live visor video, joystick control, head tracking, and a Nemotron assessment card. Fleet coordination, coverage, and two-way audio follow that core loop; beacon deployment and additional sensors are stretch features there.

This product vision additionally calls for a glove controller, voice commands, SLAM, and a visor front-camera view. Their implementation and priority still need to be incorporated into the technical plan. The existing plan uses a Bluetooth gamepad for initial joystick control.

## Lowest-priority stretch features

These features are deferred until the unfinished core features and higher-priority work in the implementation plan are complete.

- [ ] **Reconstructed environment for responders:** Build an explorable simulated environment from images captured by the robot, using mapping and pose information where available. First responders can look through this reconstruction to understand the site, inspect survivor locations, and prepare before entering. Distinguish observed areas from inferred or missing geometry and show capture times so the reconstruction is not mistaken for a complete, current view.
- [ ] **Nemotron scene-specific suggestions:** Use robot images, sensor readings, mapped surroundings, and survivor observations to suggest possible next steps for first responders. For example, when a person appears trapped under rubble, surface relevant scene observations, potential hazards, and considerations for the responding team. Tie suggestions to the available evidence, state uncertainty and missing information, and leave rescue decisions to qualified responders following their procedures.

## Decisions still needed

- Define what the controller's up/down input controls.
- Select the glove controller hardware and connection method.
- Choose a SLAM approach compatible with the robot's computing and sensing hardware.
- Confirm available infrared, thermal, light, and hazard sensors.
- Define the visor front-camera hardware and how the rescuer switches between the local view and robot feed.
- Define supported voice commands and which coordination actions require human confirmation.
