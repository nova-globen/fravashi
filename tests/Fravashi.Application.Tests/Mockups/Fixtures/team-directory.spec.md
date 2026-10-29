# Written specification: a team directory

Written for #50's acceptance clause "an agent can author a valid tree from a written specification". It
names what the screens must do in product words and deliberately says nothing about the schema's property
names, so whoever authors the tree has to translate it.

A small web app for looking up people on a team.

1. **Directory screen.** The page title is "Team directory". At the top there is a toolbar with a search
   box (placeholder "Search by name") and an "Add person" button. Below it a table lists people with three
   columns: Name, Role and Office. Use these four sample people: Ada Lovelace (Engineer, London), Grace
   Hopper (Admiral, Arlington), Alan Turing (Researcher, Manchester) and Katherine Johnson (Mathematician,
   Hampton). While the list is loading the table shows a loading state, and when nobody matches the search
   it shows "Nobody matches that search." Choosing a person in the table opens their profile screen.
2. **Add person dialog.** "Add person" opens a dialog titled "Add a person" with a form: Name (text,
   required), Role (a choice of Engineer, Researcher or Manager), Office (text), and a checkbox "Send a
   welcome email" that starts ticked. It has Save and Cancel buttons. Cancel closes the dialog. Submitting
   the form puts the Save button into a loading state showing "Saving...". If saving fails, an error message
   "Could not save this person." appears in the dialog and screen readers must announce it straight away; it
   is hidden until then.
3. **Profile screen.** Shows the person's photo (it is a portrait of the person, so it needs a description),
   their name as the main heading, and two tabs: "Details" (role and office as text) and "Activity" (a list
   of recent activity: "Joined the team", "Updated office"). A "Back to directory" button returns to the
   directory screen.
4. **Narrow screens.** The design is specified at a wide desktop width of 1280 and a phone width of 390. On
   the phone the toolbar stacks its contents vertically instead of side by side.
5. **Icon button.** The directory toolbar also has a refresh button that shows only an icon, so it must
   still have an accessible name, "Refresh".
