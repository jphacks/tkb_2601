using System;
using System.Collections.Generic;
using UnityEngine;

namespace Guardian.UnitySim
{
    /// <summary>
    /// How the shop opens, scene by scene, so the robots have work from the first second: the last guests have
    /// just gone and every place still holds the dishes they emptied, so the robots set off to clear them
    /// (Clearing); each place is offered once it is clear and the staff shows the waiting guests to it
    /// (Seating); the robots carry out what they order (Serving). When the first of them gets up to go home,
    /// the shop's usual loop runs (Regular).
    /// </summary>
    public enum GuardianOpeningScene { Clearing, Seating, Serving, Regular }

    /// <summary>
    /// The restaurant's customer loop, in the order a real shop runs it (meals included: a guest orders when
    /// they sit, the robots serve it to the place in front of their chair, they eat once it has come, and the
    /// dishes are empty when they stand up; the seat is offered again once its dishes have been cleared):
    /// a guest walks in through the 入口 sliding door, writes their name on the board and waits on a floor
    /// mark (two places). Empty dishes are sent for as soon as there are any (ClearDirtyTables), without
    /// waiting for their guest to leave or the next one to come. A diner who has finished eating stands up,
    /// pays at the self-checkout and goes home through the 出口 door; a few seconds after they are out of the
    /// door, a member of staff walks over to the first guest in line, calls them, and leads them to the free
    /// seat; the second guest moves up and the staff member goes back to the entrance. New guests keep
    /// arriving every few seconds, so the robots are rarely left without orders.
    /// At most two guests walk at once (staff aside), and nobody wanders. Aisle walking uses the same floor
    /// grid, planner and cell queues as the robots, so robots yield to them; if the planner cannot fit a
    /// route within a few seconds (robots busy in the aisles), the guest walks the side aisles instead, so
    /// the loop never stalls. Short steps by the door, the board, the line and a chair are scripted.
    /// Guest objects are pooled and reused, so nothing is allocated per customer.
    /// The loop starts with the opening scenes (GuardianOpeningScene): empty seats with dishes to clear and two
    /// guests waiting in line.
    /// </summary>
    public sealed class GuardianDinerFlow : MonoBehaviour
    {
        private enum Phase
        {
            Away, Arriving, SigningIn, ToLine, Waiting, Called, Following, StepIn, Align, Seated, Standing, StepOut, ToCheckout, Paying, ToExit, Departing,
            StaffHome, StaffToGuest, StaffCalling, StaffLeading, StaffPresenting, StaffReturning
        }
        private enum SeatState { Occupied, Clearing, Ready, Reserved }

        private sealed class Diner
        {
            public string Id;
            public GuardianPedestrianMotion Motion;
            public RiggedRestaurantGuest Guest;
            public Seat Seat, LeftSeat;
            public Vector3 Approach, Aisle, Goal;
            public Phase Phase;
            public float Until, NextBite, RetryAt, Progress, GoSince, PhaseSince;
            public GuardianLegPlan Plan;
            public int Node, AlignTries, LineSlot = -1;
            public bool Entered, Going, Fallback, Reported, Stuck;
            // The meal: what they ordered, and when they started and finish eating.
            public readonly List<DeliveryTask> Orders = new List<DeliveryTask>();
            public bool Served, Finished;
            public float SeatedAt, OrderAt, EatEnd;
            public Diner Following;  // staff: the guest being shown to a table
            public readonly List<Vector3> Path = new List<Vector3>();
            public int PathAt;
            public Phase Last;
        }

        private sealed class Seat
        {
            public Transform Chair;
            public int Table;
            public int Place;  // the chair's corner of the table (0-3): where its guest's meal is set down
            public SeatState State;
            public float ReadyAt;
            public bool Departed;
            public TextMesh Label;
            public int Shown = -1;
        }

        // Chairs whose short step out to the aisle stays clear of the other chairs and the booths.
        private static readonly string[] SeatNames = { "Chair 1-1-0", "Chair 1-1-1", "Chair 2-1-0", "Chair 3-1-0", "Chair 2-0-1" };
        private const float MealOnTheWayWait = 90;  // seconds a guest stays on for a meal a robot is already carrying
        private const int Pool = 12;  // up to eight at the tables, two waiting, one arriving, one leaving
        private const int OpeningGuests = 4;  // already seated (two per ready table) when the shop opens
        private const float AisleOffset = .94f, StepSpeed = .55f, WalkSpeed = .95f, SeatAfterLeaving = 2;
        // The first guest after the two already waiting walks in this soon, and the next ones this far apart:
        // quick enough that the robots are rarely left without orders.
        private const float FirstArrival = 2, ArrivalGapMin = 3, ArrivalGapMax = 6;
        // A guest waits this long for a meal before eating what has come (and gives up later still). A seat is offered
        // only once its place is clear, as staff in a restaurant clear the table before showing the next guests to it.
        private const float MealWait = 120;
        private static readonly GuardianMenu[] Mains = { GuardianMenu.CurryRice, GuardianMenu.Noodles, GuardianMenu.Hamburg };
        // Side aisles used when the planner has no room: along the south wall, and down the east wall.
        private const float SouthAisleZ = -4.65f, EastAisleX = 8.75f;
        /// <summary>Where the member of staff waits, just inside the entrance and clear of the doorway.</summary>
        private static Vector3 StaffHome => new Vector3(RestaurantEnclosure.EntranceX + .1f, 0, RestaurantEnclosure.South + 1.75f);

        private readonly List<Diner> diners = new List<Diner>();
        private readonly List<Seat> seats = new List<Seat>();
        private readonly Diner[] line = new Diner[2];
        private readonly List<GameObject> hiddenStatic = new List<GameObject>();
        private readonly FloorFootprint footprint = new FloorFootprint(0, 0, .55f, .55f);
        private GuardianSimulator sim;
        private GuardianDirectTraffic traffic;
        private System.Random random;
        private Diner leaving, called, arriving, staff;
        private float nextArrival;
        private float nextSweep;  // when ClearDirtyTables next looks for empty dishes
        private int tableCount;
        private TextMesh sign, staffTag;
        private Font font;
        public bool Running { get; private set; }
        public GuardianOpeningScene Scene { get; private set; } = GuardianOpeningScene.Regular;
        /// <summary>The opening scenes are still playing (the simulator holds its condition events back meanwhile).</summary>
        public bool Opening => Running && Scene != GuardianOpeningScene.Regular;
        /// <summary>A guest on the floor, as the core's screen shows it (GuardianHubLink): a short state in Japanese.</summary>
        public struct GuestView { public string Id, State; public Vector3 At; }
        /// <summary>A chair the guests use: occupied / clearing / ready / reserved, and its table (1-3).</summary>
        public struct SeatView { public string Id, State; public int Table; }
        /// <summary>Where the member of staff waits between guests.</summary>
        public static Vector3 StaffPost => StaffHome;

        /// <summary>Copies who is on the floor and how each chair stands into the lists (read only; for GuardianHubLink).</summary>
        public void Snapshot(List<GuestView> guests, List<SeatView> chairs)
        {
            guests.Clear(); chairs.Clear();
            if (!Running) return;
            foreach (var d in diners)
            {
                string state = GuestState(d);
                if (state == null || d.Motion == null || d.Motion.Body == null) continue;
                guests.Add(new GuestView { Id = d.Id, State = state, At = d.Motion.Body.position });
            }
            foreach (var s in seats)
            {
                if (s.Chair == null) continue;
                string state = s.State == SeatState.Occupied ? "occupied" : s.State == SeatState.Clearing ? "clearing" : s.State == SeatState.Ready ? "ready" : "reserved";
                chairs.Add(new SeatView { Id = s.Chair.name, State = state, Table = s.Table + 1 });
            }
        }

        private static string GuestState(Diner d)
        {
            switch (d.Phase)
            {
                case Phase.Arriving: case Phase.SigningIn: return "来店";
                case Phase.ToLine: case Phase.Waiting: return "席待ち";
                case Phase.Called: case Phase.Following: case Phase.StepIn: case Phase.Align: return "ご案内";
                case Phase.Seated: return !d.Served ? "注文待ち" : !d.Finished ? "食事中" : "食べ終わり";
                case Phase.Standing: case Phase.StepOut: return "席を立つ";
                case Phase.ToCheckout: case Phase.Paying: return "お会計";
                case Phase.ToExit: case Phase.Departing: return "お帰り";
                default: return null;  // away, or the member of staff
            }
        }

        /// <summary>Where the member of staff is, and whether they are busy showing a guest to a table (for GuardianHubLink).</summary>
        public bool TryGetStaff(out Vector3 position, out bool busy)
        {
            position = Vector3.zero; busy = false;
            if (!Running || staff == null || staff.Motion == null || staff.Motion.Body == null) return false;
            position = staff.Motion.Body.position; busy = staff.Phase != Phase.StaffHome;
            return true;
        }
        /// <summary>A guest comes in; a guest starts paying; a waiting guest is called to a table (seat, table number).</summary>
        public event Action<Vector3> GuestEntered, GuestPaying;
        public event Action<Vector3, int> GuestCalled;
        /// <summary>A diner gets up to go home (table number); a guest goes out of the door.</summary>
        public event Action<Vector3, int> GuestLeaving;
        public event Action<Vector3> GuestLeft;
        /// <summary>Guests waiting on the floor marks (for captions and tests).</summary>
        public int Waiting => (line[0] != null ? 1 : 0) + (line[1] != null ? 1 : 0);

        private static FloorPoint Flat(Vector3 p) => new FloorPoint(p.x, p.z);
        private float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

        public void ResetDiners(GuardianSimulator simulator, bool active)
        {
            if (sim != null) sim.PhysicsStepBeginning -= Step;
            foreach (var d in diners) { traffic?.Remove(d.Id); if (d.Motion != null) Destroy(d.Motion.gameObject); }
            if (staff != null) { traffic?.Remove(staff.Id); if (staff.Motion != null) Destroy(staff.Motion.gameObject); }
            foreach (var s in seats) if (s.Label != null) Destroy(s.Label.gameObject);
            diners.Clear(); seats.Clear(); line[0] = line[1] = null; staff = null; staffTag = null;
            foreach (var go in hiddenStatic) if (go != null) go.SetActive(true);
            hiddenStatic.Clear();
            sim = simulator; traffic = sim.Traffic.Direct; Running = false; leaving = called = arriving = null;
            Scene = GuardianOpeningScene.Regular;
            if (sign != null) sign.gameObject.SetActive(false);
            if (!active || traffic == null) return;
            Transform environment = sim.transform.Find("Generated 3D World/" + RestaurantVisuals.RootName);
            var catalog = Resources.Load<RestaurantCatalog>("RestaurantCatalog");
            if (environment == null || catalog == null) return;
            var models = catalog.DinerGuests != null && catalog.DinerGuests.Length > 0 ? catalog.DinerGuests : new[] { catalog.WalkingGuest };
            random = new System.Random(sim.Settings.Seed ^ 0x5eed);
            EnsureFont();
            // The rigged diners take the place of the static seated figures.
            foreach (RestaurantPerson person in environment.GetComponentsInChildren<RestaurantPerson>())
                if (person.Seat != null && person.gameObject.activeSelf) { person.gameObject.SetActive(false); hiddenStatic.Add(person.gameObject); }
            foreach (string name in SeatNames)
            {
                Transform chair = environment.Find(name);
                if (chair == null) continue;
                int table = name[6] - '1';
                // Which corner of the table the chair is at, in the table's own frame: the place for its guest's meal.
                Transform top = environment.Find("Dining Table " + (table + 1));
                Vector3 local = top != null ? top.InverseTransformPoint(chair.position) : Vector3.zero;
                seats.Add(new Seat { Chair = chair, Table = table, Place = (local.x < 0 ? 0 : 1) + (local.z < 0 ? 0 : 2), State = SeatState.Ready });
            }
            for (int i = 0; i < Pool; i++)
            {
                var d = Spawn("Diner-" + (i + 1), models[i % models.Length]);
                if (d != null) diners.Add(d);
            }
            // The member of staff who shows guests to their table.
            staff = Spawn("Staff", catalog.WalkingGuest != null ? catalog.WalkingGuest : models[0]);
            if (staff != null)
            {
                staff.Motion.gameObject.SetActive(true);
                Place(staff, StaffHome, Quaternion.Euler(0, -30, 0));
                Hold(staff, StaffHome); staff.Phase = Phase.StaffHome;
                staffTag = Tag(staff.Motion.transform, "店員", new Color(.55f, .85f, 1f));
            }
            // Opening, scene 1 (clearing): the last guests have just gone from the last table, which still holds what
            // they ate from, so a robot sets off to clear it on the first physics step; a seat there is offered again as
            // soon as its place is clear. The other tables are ready and already have a guest each, who orders at once,
            // so the other robots carry meals meanwhile (a meal for a table does not block the clearing of another),
            // and the line is shown to the free seats.
            tableCount = 0;
            foreach (var seat in seats) tableCount = Mathf.Max(tableCount, seat.Table + 1);
            int clearingTable = tableCount - 1;
            foreach (var seat in seats)
            {
                if (seat.Table != clearingTable) continue;
                LeaveDishes(seat);
                seat.State = SeatState.Clearing; seat.Departed = true; seat.ReadyAt = sim.DemoClock;
            }
            for (int slot = 0; slot < line.Length && slot < diners.Count; slot++)
            {
                var d = diners[slot];
                d.Motion.gameObject.SetActive(true);
                d.LineSlot = slot; line[slot] = d; Settle(d, sim.DemoClock);
            }
            // Two guests already at each ready table, ordering at once: the shop is busy from the first second.
            int next = line.Length, seated = 0;
            var perTable = new Dictionary<int, int>();
            foreach (var seat in seats)
            {
                if (next >= diners.Count || seated >= OpeningGuests) break;
                perTable.TryGetValue(seat.Table, out int atTable);
                if (seat.State != SeatState.Ready || atTable >= 2) continue;
                perTable[seat.Table] = atTable + 1; seated++;
                var d = diners[next++];
                d.Motion.gameObject.SetActive(true);
                TakeSeat(d, seat);
                if (!SitNow(d, sim.DemoClock))
                {
                    seat.State = SeatState.Ready; d.Seat = null;
                    d.Motion.gameObject.SetActive(false); Enter(d, Phase.Away, sim.DemoClock);
                }
            }
            nextArrival = sim.DemoClock + FirstArrival;
            Scene = GuardianOpeningScene.Clearing; nextSweep = sim.DemoClock;
            EnsureSign(environment);
            sim.PhysicsStepBeginning += Step;
            Running = true;
        }

        private Diner Spawn(string id, GameObject prefab)
        {
            if (prefab == null) return null;
            var root = Instantiate(prefab, transform);
            root.name = id;
            var guest = root.GetComponent<RiggedRestaurantGuest>();
            if (guest == null) { Destroy(root); return null; }
            var motion = root.AddComponent<GuardianPedestrianMotion>();
            motion.StartPoint = motion.EndPoint = RestaurantEnclosure.StreetWest;
            motion.Initialize();
            root.SetActive(false);
            return new Diner { Id = id, Motion = motion, Guest = guest, Phase = Phase.Away };
        }

        /// <summary>Give the guest a chair: where they stand to sit down, and the aisle point beside it.</summary>
        private void TakeSeat(Diner d, Seat seat)
        {
            d.Seat = seat;
            seat.State = SeatState.Reserved;
            d.Approach = seat.Chair.TransformPoint(d.Guest.RigSettings.Approach);
            float side = seat.Chair.name.EndsWith("-0") ? -1 : 1;  // the chair nearer the table's south end steps south
            d.Aisle = d.Approach + new Vector3(0, 0, side * AisleOffset);
        }

        private static void Place(Diner d, Vector3 at, Quaternion rotation)
        {
            d.Motion.transform.SetPositionAndRotation(at, rotation);
            d.Motion.Body.position = at; d.Motion.Body.rotation = rotation;
        }

        private static void Enter(Diner d, Phase phase, float clock) { d.Phase = phase; d.PhaseSince = clock; }

        /// <summary>Put the guest exactly at the chair's approach, facing the table, and start sitting down; they order once seated.</summary>
        private bool SitNow(Diner d, float clock)
        {
            d.Motion.Wait();
            Place(d, d.Approach, d.Seat.Chair.rotation);
            if (!d.Guest.BindSeat(d.Seat.Chair) || !d.Guest.Play(GuestPose.SitDown)) return false;
            traffic.Remove(d.Id);
            d.Motion.Shape.enabled = false;  // seated guests are part of the furniture, not an obstacle for robots
            d.Seat.State = SeatState.Occupied;
            Enter(d, Phase.Seated, clock); d.Until = float.MaxValue; d.NextBite = clock + Range(3, 6);
            d.Orders.Clear(); d.Served = d.Finished = false; d.SeatedAt = d.OrderAt = clock;
            sim.Inventory?.ReserveGuestPlace(d.Seat.Table, d.Seat.Place, true);
            return true;
        }

        // ---- the meal ----

        /// <summary>The place (0-3) of a guest seated at this table who has not finished eating, or -1: for an order made on their behalf.</summary>
        public int GuestPlace(int table)
        {
            foreach (var d in diners)
                if (d.Phase == Phase.Seated && d.Seat != null && d.Seat.Table == table && !d.Finished) return d.Seat.Place;
            return -1;
        }

        /// <summary>What the last guest at this seat left behind at the opening: an emptied main dish, and a soup bowl about half the time.</summary>
        private void LeaveDishes(Seat seat)
        {
            var inventory = sim.Inventory;
            if (inventory == null) return;
            inventory.LeaveAtPlace(seat.Table, seat.Place, Mains[random.Next(Mains.Length)]);
            if (random.NextDouble() < .5) inventory.LeaveAtPlace(seat.Table, seat.Place, GuardianMenu.Soup);
        }

        /// <summary>A main dish, and soup about half the time, for the place in front of their chair.</summary>
        private void Order(Diner d)
        {
            if (d.Seat == null) return;
            int table = d.Seat.Table, place = d.Seat.Place;
            void Add(DeliveryKind kind, GuardianMenu menu)
            {
                try { var task = sim.QueueTask(kind, table, menu, null, place); if (task != null) d.Orders.Add(task); }
                catch (Exception e) { Debug.LogWarning("[GuardianDiners] order for table " + (table + 1) + " not taken: " + e.Message); }
            }
            Add(DeliveryKind.Food, Mains[random.Next(Mains.Length)]);
            if (random.NextDouble() < .5) Add(DeliveryKind.Soup, GuardianMenu.Soup);
        }

        /// <summary>Seated: order, wait for the meal, eat it (reaching for it now and then), and empty the dishes.</summary>
        private void Eat(Diner d, float clock)
        {
            if (!d.Served)
            {
                if (d.Orders.Count == 0 && clock >= d.OrderAt) { Order(d); d.OrderAt = clock + 5; }  // a full kitchen: ask again shortly
                bool any = false, all = d.Orders.Count > 0;
                foreach (var t in d.Orders) { bool done = t.Phase == TaskPhase.Completed; any |= done; all &= done; }
                if (all || (any && clock - d.SeatedAt > MealWait))
                { d.Served = true; d.EatEnd = clock + Range(25, 40); d.Until = d.EatEnd + 2.5f; d.NextBite = clock + Range(1, 2.5f); }
                else if (clock - d.SeatedAt > MealWait + 30 && !MealOnTheWay(d, clock)) { d.Served = d.Finished = true; d.Until = clock; }  // nothing came: they leave
                return;
            }
            if (d.Finished) return;
            if (clock >= d.EatEnd) FinishMeal(d);
            else if (clock >= d.NextBite) { d.Guest.Play(GuestPose.Handoff); d.NextBite = clock + Range(3.5f, 7); }  // reaching for the food
        }

        /// <summary>The guest has eaten: the dishes in front of them are empty.</summary>
        private void FinishMeal(Diner d)
        {
            d.Finished = true;
            if (d.Seat != null) sim.Inventory?.FinishMeals(d.Seat.Table, d.Seat.Place);
        }

        private void Step(float clock)
        {
            if (!Running) return;
            GuardianFrameMarker.Enter("people: customers");
            float dt = Time.fixedDeltaTime;
            bool planned = false;  // at most one planner request per physics step
            bool paying = false;
            if (clock >= nextSweep) { nextSweep = clock + 1; ClearDirtyTables(); KeepStaffClearOfGuests(); }
            UpdateSeats(clock);
            // At most two guests walking at once; showing a waiting guest to a free table comes first,
            // then the next diner getting up, then a new arrival.
            if (called == null && staff != null && staff.Phase == Phase.StaffHome && line[0] != null && line[0].Phase == Phase.Waiting && Walking() < 2) CallNext(clock);
            if (leaving == null && Walking() < 2) leaving = NextToLeave(clock);
            if (arriving == null && clock >= nextArrival && Walking() < 2) Arrive(clock);
            UpdateScene();
            foreach (var d in diners)
            {
                // One guest's trouble must never stop the others (or the robots' physics step).
                try { if (StepGuest(d, clock, dt, ref planned)) paying = true; }
                catch (Exception e) { if (!d.Reported) { d.Reported = true; Debug.LogException(e); } }
                Watch(d, clock);
            }
            if (staff != null)
            {
                try { StepStaff(staff, clock, dt, ref planned); }
                catch (Exception e) { if (!staff.Reported) { staff.Reported = true; Debug.LogException(e); } }
                Watch(staff, clock);
            }
            if (sign != null)
            {
                if (!sign.gameObject.activeSelf) sign.gameObject.SetActive(true);
                string text = paying ? "お会計中…" : "セルフレジ";
                if (sign.text != text) { sign.text = text; sign.color = paying ? new Color(1f, .82f, .3f) : Color.white; }
            }
        }

        /// <summary>One guest's step; true while they are paying.</summary>
        private bool StepGuest(Diner d, float clock, float dt, ref bool planned)
        {
            switch (d.Phase)
            {
                case Phase.Away:
                    return false;
                case Phase.Arriving:
                    // Street → through the entrance → the sign-in board.
                    bool done = FollowPath(d, dt, WalkSpeed);
                    if (!d.Entered && d.PathAt >= 2) { d.Entered = true; GuestEntered?.Invoke(RestaurantEnclosure.EntranceInside); }
                    if (done)
                    {
                        Face(d, Quaternion.Euler(0, 180, 0));  // facing the board
                        d.Guest.Play(GuestPose.HandoffStanding);  // writing their name
                        Enter(d, Phase.SigningIn, clock); d.Until = clock + 2.2f;
                    }
                    break;
                case Phase.SigningIn:
                    if (clock >= d.Until) { PathToLine(d); d.Motion.Walk(); Enter(d, Phase.ToLine, clock); }
                    break;
                case Phase.ToLine:
                    if (FollowPath(d, dt, WalkSpeed)) { Settle(d, clock); if (arriving == d) arriving = null; }
                    break;
                case Phase.Waiting:
                    // The second in line steps up when the first has gone with the staff.
                    if (d.LineSlot == 1 && line[0] == null)
                    {
                        line[1] = null; line[0] = d; d.LineSlot = 0;
                        traffic.Remove(d.Id);
                        d.Path.Clear(); d.Path.Add(RestaurantEnclosure.Line[0]); d.PathAt = 0;
                        d.Motion.Walk(); Enter(d, Phase.ToLine, clock);
                    }
                    break;
                case Phase.Called:
                    break;  // the staff member is on the way; StepStaff moves them on
                case Phase.Following:
                    if (clock < d.Until) break;  // a step behind the staff member
                    if (Go(d, d.Aisle, clock, ref planned)) { d.Guest.Play(GuestPose.Walk); Enter(d, Phase.StepIn, clock); }
                    break;
                case Phase.StepIn:
                    if (Glide(d, d.Approach, dt, StepSpeed)) { Enter(d, Phase.Align, clock); d.AlignTries = 0; }
                    break;
                case Phase.Align:
                    // One physics step after the last scripted move, so no pending MovePosition undoes the seat.
                    if (SitNow(d, clock)) { if (called == d) called = null; }  // they stay until they have eaten what they order
                    else if (++d.AlignTries > 20)
                    {
                        Debug.LogWarning("[GuardianDiners] " + d.Id + " could not sit; leaving instead");
                        if (called == d) called = null;
                        if (d.Seat != null) { d.Seat.State = SeatState.Ready; d.Seat = null; }
                        Enter(d, Phase.ToCheckout, clock); d.Motion.Shape.enabled = true; d.Motion.Walk();
                    }
                    break;
                case Phase.Seated:
                    if (d.Guest.Pose != GuestPose.Seated) break;
                    if (d == leaving)
                    {
                        if (!d.Finished) FinishMeal(d);  // the plate is empty before they get up
                        TakeBackOrders(d);               // nothing they ordered is served to the empty chair
                        if (d.Guest.Play(GuestPose.StandUp))
                        {
                            Enter(d, Phase.Standing, clock);
                            if (d.Seat != null) GuestLeaving?.Invoke(d.Seat.Chair.position, d.Seat.Table + 1);
                        }
                    }
                    else Eat(d, clock);
                    break;
                case Phase.Standing:
                    // RiggedRestaurantGuest finishes standing on its own and puts the guest at the approach.
                    if (d.Guest.HasSeat || clock < d.RetryAt) break;
                    d.RetryAt = clock + .5f;
                    if (!AisleFree(d)) break;  // wait at the table until the aisle point is clear
                    Place(d, d.Motion.transform.position, d.Motion.transform.rotation);
                    d.Motion.Shape.enabled = true;
                    d.Guest.Play(GuestPose.Walk);
                    Enter(d, Phase.StepOut, clock);
                    break;
                case Phase.StepOut:
                    if (Glide(d, d.Aisle, dt, StepSpeed))
                    {
                        Hold(d, d.Aisle);
                        if (d.Seat != null) { sim.Inventory?.ReserveGuestPlace(d.Seat.Table, d.Seat.Place, false); StartClearing(d.Seat); d.LeftSeat = d.Seat; d.Seat = null; }
                        Enter(d, Phase.ToCheckout, clock); d.RetryAt = clock;
                    }
                    break;
                case Phase.ToCheckout:
                    if (Go(d, RestaurantEnclosure.CheckoutSpot, clock, ref planned))
                    { Face(d, Quaternion.Euler(0, 180, 0)); Enter(d, Phase.Paying, clock); d.Until = clock + Range(3, 5); GuestPaying?.Invoke(RestaurantEnclosure.Kiosk); }
                    break;
                case Phase.Paying:
                    if (clock >= d.Until) { Enter(d, Phase.ToExit, clock); d.RetryAt = clock; }
                    return true;
                case Phase.ToExit:
                    if (Go(d, RestaurantEnclosure.ExitInside, clock, ref planned))
                    {
                        traffic.Remove(d.Id);
                        d.Path.Clear(); d.Path.Add(RestaurantEnclosure.ExitOutside); d.Path.Add(RestaurantEnclosure.StreetEast); d.PathAt = 0;
                        d.Motion.Walk(); Enter(d, Phase.Departing, clock);
                        // Out of the door: their seat is offered to the next guest a few seconds from now.
                        if (d.LeftSeat != null) { d.LeftSeat.ReadyAt = Mathf.Min(d.LeftSeat.ReadyAt, clock + SeatAfterLeaving); d.LeftSeat.Departed = true; d.LeftSeat = null; }
                        if (leaving == d) leaving = null;  // the next diner may get up now
                        GuestLeft?.Invoke(RestaurantEnclosure.ExitOutside);
                    }
                    break;
                case Phase.Departing:
                    // Walking home along the pavement, then gone.
                    if (FollowPath(d, dt, WalkSpeed)) { d.Motion.Wait(); d.Motion.gameObject.SetActive(false); Enter(d, Phase.Away, clock); }
                    break;
            }
            if (d.Phase != Phase.Away && d.Phase != Phase.Seated) KeepDoorsOpen(d);
            return false;
        }

        /// <summary>The member of staff: walk to the waiting guest, call them, lead them to the seat, go back.</summary>
        private void StepStaff(Diner s, float clock, float dt, ref bool planned)
        {
            switch (s.Phase)
            {
                case Phase.StaffToGuest:
                    if (FollowPath(s, dt, WalkSpeed))
                    {
                        var guest = s.Following;
                        if (guest != null) Face(s, Quaternion.LookRotation(Flatten(guest.Motion.Body.position - s.Motion.Body.position)));
                        s.Guest.Play(GuestPose.HandoffStanding);  // "お待たせしました、こちらへどうぞ"
                        if (guest != null && guest.Seat != null) GuestCalled?.Invoke(guest.Seat.Chair.position, guest.Seat.Table + 1);
                        Enter(s, Phase.StaffCalling, clock); s.Until = clock + .8f;
                    }
                    break;
                case Phase.StaffCalling:
                    if (clock < s.Until) break;
                    var g = s.Following;
                    if (g != null)
                    {
                        // The guest leaves the line and follows; the second in line moves up.
                        if (line[0] == g) line[0] = null;
                        g.LineSlot = -1; traffic.Remove(g.Id); g.Going = false;
                        Enter(g, Phase.Following, clock); g.Until = clock + 1.2f;
                    }
                    s.Going = false; s.Motion.Walk(); Enter(s, Phase.StaffLeading, clock);
                    break;
                case Phase.StaffLeading:
                    if (Go(s, StaffSpot(s.Following), clock, ref planned))
                    {
                        var to = s.Following != null && s.Following.Seat != null ? s.Following.Seat.Chair.position : s.Motion.Body.position + Vector3.forward;
                        Face(s, Quaternion.LookRotation(Flatten(to - s.Motion.Body.position)));
                        s.Guest.Play(GuestPose.HandoffStanding);  // "こちらのお席です"
                        Enter(s, Phase.StaffPresenting, clock); s.Until = clock + 1;
                    }
                    break;
                case Phase.StaffPresenting:
                    var f = s.Following;
                    bool seated = f == null || f.Phase == Phase.StepIn || f.Phase == Phase.Align || f.Phase == Phase.Seated;
                    if (clock >= s.Until && (seated || clock - s.PhaseSince > 12))
                    { s.Following = null; s.Going = false; s.Motion.Walk(); Enter(s, Phase.StaffReturning, clock); }
                    break;
                case Phase.StaffReturning:
                    if (Go(s, StaffHome, clock, ref planned))
                    { Place(s, StaffHome, Quaternion.Euler(0, -30, 0)); Hold(s, StaffHome); Enter(s, Phase.StaffHome, clock); }
                    break;
            }
        }

        /// <summary>Where the staff member stands to show a seat: beside its aisle point, on the side away from the table.</summary>
        private static Vector3 StaffSpot(Diner guest)
        {
            if (guest == null || guest.Seat == null) return StaffHome;
            float away = guest.Seat.Chair.position.x > 6.1f ? 1 : -1;  // tables run along x = 6.1
            return guest.Aisle + new Vector3(away * .8f, 0, 0);
        }

        private static Vector3 Flatten(Vector3 v) { v.y = 0; return v.sqrMagnitude < 1e-6f ? Vector3.forward : v; }

        private int Walking()
        {
            int n = 0;
            foreach (var d in diners)
                switch (d.Phase)
                {
                    case Phase.Arriving: case Phase.SigningIn: case Phase.Following: case Phase.StepIn: case Phase.Standing:
                    case Phase.StepOut: case Phase.ToCheckout: case Phase.Paying: case Phase.ToExit: case Phase.Departing: n++; break;
                    case Phase.ToLine: if (d == arriving) n++; break;
                }
            return n;
        }

        /// <summary>Names a guest who has been in the same step for 40 s (except while eating, waiting or away) once, for the console.</summary>
        private void Watch(Diner d, float clock)
        {
            if (d.Phase != d.Last) { d.Last = d.Phase; d.Stuck = false; return; }
            if (d.Stuck || clock - d.PhaseSince < 40) return;
            if (d.Phase == Phase.Seated || d.Phase == Phase.Waiting || d.Phase == Phase.Away || d.Phase == Phase.StaffHome || d.Phase == Phase.Called) return;
            d.Stuck = true;
            Vector3 p = d.Motion.Body.position;
            Debug.LogWarning($"[GuardianDiners] {d.Id} has been in {d.Phase} for 40 s at ({p.x:0.0}, {p.z:0.0}); planner: {traffic.LastFailure}");
        }

        // ---- arriving and waiting ----

        /// <summary>The next guest walks in from the street, if a place in line is free.</summary>
        private void Arrive(float clock)
        {
            // The back of the line: if the front place is empty because its guest just left with the staff,
            // the second guest steps up first and the newcomer takes the second place (no jumping the line).
            int slot = line[0] == null && line[1] == null ? 0 : line[1] == null && line[0] != null ? 1 : -1;
            if (slot < 0) { nextArrival = clock + 2; return; }
            Diner d = diners.Find(x => x.Phase == Phase.Away);
            if (d == null) { nextArrival = clock + 2; return; }
            d.Motion.gameObject.SetActive(true);
            d.Motion.Shape.enabled = true;
            d.Reported = false;
            Place(d, RestaurantEnclosure.StreetWest, Quaternion.Euler(0, 90, 0));
            d.LineSlot = slot; line[slot] = d;  // the place is kept for them while they come in
            d.Path.Clear();
            d.Path.Add(RestaurantEnclosure.EntranceOutside); d.Path.Add(RestaurantEnclosure.EntranceInside); d.Path.Add(RestaurantEnclosure.BoardSpot);
            d.PathAt = 0;
            d.Motion.Walk(); Enter(d, Phase.Arriving, clock); arriving = d; d.Entered = false;
            nextArrival = clock + Range(ArrivalGapMin, ArrivalGapMax);
        }

        /// <summary>From the board to the guest's floor mark; the second mark is reached round the first, not through the person on it.</summary>
        private static void PathToLine(Diner d)
        {
            var to = RestaurantEnclosure.Line[d.LineSlot];
            d.Path.Clear(); d.PathAt = 0;
            if (d.LineSlot == 1)
            {
                Vector3 front = RestaurantEnclosure.Line[0];
                d.Path.Add(new Vector3(RestaurantEnclosure.BoardSpot.x - .25f, 0, front.z + 1));
                d.Path.Add(new Vector3(to.x, 0, front.z + 1));
            }
            d.Path.Add(to);
        }

        /// <summary>Stand on the floor mark facing the dining room, and hold the spot on the floor grid.</summary>
        private void Settle(Diner d, float clock)
        {
            var at = RestaurantEnclosure.Line[d.LineSlot];
            Place(d, at, Quaternion.Euler(0, 60, 0));
            Hold(d, at);
            Enter(d, Phase.Waiting, clock);
        }

        /// <summary>
        /// The member of staff walks among the guests without bodily pushing against them, so they never stand against a
        /// guest with their legs still walking; robots still see and avoid them. Unity forgets the setting when a guest's
        /// collider is switched off and on (seated, then leaving), so it is set again once a second.
        /// </summary>
        private void KeepStaffClearOfGuests()
        {
            if (staff == null) return;
            foreach (var d in diners)
                if (d.Motion.Shape.enabled && staff.Motion.Shape.enabled) Physics.IgnoreCollision(staff.Motion.Shape, d.Motion.Shape, true);
        }

        /// <summary>A free seat and a guest at the front of the line: send the staff member to call them.</summary>
        private void CallNext(float clock)
        {
            Seat seat = seats.Find(x => x.State == SeatState.Ready);
            if (seat == null) return;
            var d = line[0];
            TakeSeat(d, seat);
            Enter(d, Phase.Called, clock);
            called = d;
            staff.Following = d;
            traffic.Remove(staff.Id);
            staff.Path.Clear(); staff.PathAt = 0;
            staff.Path.Add(RestaurantEnclosure.Line[0] + new Vector3(.75f, 0, .6f));  // beside the guest, on the room side
            staff.Motion.Walk(); Enter(staff, Phase.StaffToGuest, clock);
        }

        // ---- clearing ----

        /// <summary>
        /// Once a second: empty dishes on any table are sent for at once, whoever left them (a guest who has just
        /// eaten and is still seated, the opening's last guests, a meal nobody sat down to), instead of waiting for
        /// the guest to leave or the next one to come. One clearing job per tray-load not yet covered by a job that
        /// waits for a robot. The return counter takes one robot at a time, so a job waits for it before a robot
        /// sets off (a robot queueing at a table would block the aisle); the other robots stay free to serve.
        /// </summary>
        private void ClearDirtyTables()
        {
            var inventory = sim.Inventory;
            if (inventory == null) return;
            for (int table = 0; table < tableCount; table++)
            {
                int dirty = 0, waiting = 0;
                foreach (var item in inventory.AvailableDirty(table)) dirty++;
                if (dirty == 0) continue;
                foreach (var t in sim.Tasks)
                    if (t.Kind == DeliveryKind.EmptyDishes && t.TableIndex == table && t.Phase == TaskPhase.Pending && t.DishIds.Count == 0) waiting++;
                if (dirty <= waiting * GuardianDishInventory.TrayCapacity) continue;
                try { sim.QueueTask(DeliveryKind.EmptyDishes, table); }
                catch (Exception e) { Debug.LogWarning("[GuardianDiners] no clearing job for table " + (table + 1) + ": " + e.Message); }
            }
        }

        // ---- the opening ----

        /// <summary>
        /// Moves the opening on: the first guest called to a cleared seat (seating), the first meal on its way to
        /// a guest (serving), the first guest getting up to go home (the usual loop).
        /// </summary>
        private void UpdateScene()
        {
            if (Scene == GuardianOpeningScene.Regular) return;
            if (leaving != null) Scene = GuardianOpeningScene.Regular;
            else if (Scene == GuardianOpeningScene.Clearing && called != null) Scene = GuardianOpeningScene.Seating;
            else if (Scene == GuardianOpeningScene.Seating && MealOnItsWay()) Scene = GuardianOpeningScene.Serving;
        }

        private bool MealOnItsWay()
        {
            foreach (var d in diners)
                foreach (var t in d.Orders)
                    if (t.Phase == TaskPhase.ToDropoff || t.Phase == TaskPhase.Completed) return true;
            return false;
        }

        // ---- the seat between guests ----

        /// <summary>The diner has left the chair: it is offered again once its place is clear (ClearDirtyTables sends for the dishes).</summary>
        private void StartClearing(Seat seat)
        {
            seat.State = SeatState.Clearing; seat.Departed = false;
            seat.ReadyAt = float.MaxValue;  // set to "a few seconds after they are out of the door"
        }

        private void UpdateSeats(float clock)
        {
            foreach (var s in seats)
            {
                // Offered again once its dishes are cleared (or, if no robot has come, a while after its countdown).
                if (s.State == SeatState.Clearing && clock >= s.ReadyAt && (sim.Inventory == null || sim.Inventory.PlaceClear(s.Table, s.Place)))
                    s.State = SeatState.Ready;
                Label(s, clock);
            }
        }

        /// <summary>Over a chair: "片付け中" while its guest is leaving, then a countdown to the next guest, then "空席".</summary>
        private void Label(Seat s, float clock)
        {
            int key = s.State == SeatState.Clearing ? (s.Departed && clock < s.ReadyAt ? 1000 + Mathf.CeilToInt(s.ReadyAt - clock) : 1) : s.State == SeatState.Ready ? 2 : 0;
            if (key == s.Shown) return;  // rebuild the text only when it changes
            s.Shown = key;
            if (key == 0) { if (s.Label != null) s.Label.gameObject.SetActive(false); return; }
            if (s.Label == null)
            {
                s.Label = Tag(transform, "", Color.white);
                s.Label.transform.position = s.Chair.position + Vector3.up * 1.5f;
            }
            s.Label.gameObject.SetActive(true);
            s.Label.text = key == 1 ? "片付け中" : key == 2 ? "空席" : "ご案内まで " + (key - 1000) + " 秒";
            s.Label.color = key == 2 ? new Color(.45f, .95f, .55f) : new Color(1f, .75f, .3f);
        }

        // ---- leaving ----

        /// <summary>The seated guest whose meal ended first (only one guest is on the way out at a time).</summary>
        private Diner NextToLeave(float clock)
        {
            Diner next = null;
            foreach (var d in diners)
                if (d.Phase == Phase.Seated && d.Guest.Pose == GuestPose.Seated && clock >= d.Until && !MealOnTheWay(d, clock)
                    && (next == null || d.Until < next.Until)) next = d;
            return next;
        }

        /// <summary>
        /// A robot is carrying something this guest ordered: they stay seated for it (up to MealOnTheWayWait past their
        /// time), so it is not put down in front of an empty chair. Off in recorded verifications (KeepServing).
        /// </summary>
        private bool MealOnTheWay(Diner d, float clock)
        {
            if (!sim.Settings.KeepServing) return false;
            float due = d.Until == float.MaxValue ? d.SeatedAt + MealWait + 30 : d.Until;   // when they would have got up
            if (clock > due + MealOnTheWayWait) return false;
            foreach (var t in d.Orders) if (t.Phase == TaskPhase.ToDropoff) return true;
            return false;
        }

        /// <summary>The guest is getting up: orders not yet picked up are taken back (GuardianSimulator.WithdrawOrder).</summary>
        private void TakeBackOrders(Diner d)
        {
            foreach (var t in d.Orders)
                if (t.Phase != TaskPhase.Completed && sim.WithdrawOrder(t, "guest " + d.Id + " left before it was picked up"))
                    Debug.Log("[GuardianDiners] " + d.Id + " left; order " + t.Id + " taken back");
        }

        /// <summary>No robot or other person stands within reach of this guest's aisle point (distance only, no route search).</summary>
        private bool AisleFree(Diner d)
        {
            foreach (var r in sim.Robots) if (Near(r.PhysicsDriver.Body.position, d.Aisle, 1.1f)) return false;
            if (sim.Walkers != null) foreach (var w in sim.Walkers.People) if (w.Motion != null && Near(w.Motion.Body.position, d.Aisle, .9f)) return false;
            foreach (var o in diners) if (o != d && o.Phase != Phase.Seated && o.Phase != Phase.Away && Near(o.Motion.Body.position, d.Aisle, .9f)) return false;
            if (staff != null && staff.Phase != Phase.StaffHome && Near(staff.Motion.Body.position, d.Aisle, .9f)) return false;
            return true;
        }

        private static bool Near(Vector3 a, Vector3 b, float distance) { a.y = b.y = 0; return (a - b).sqrMagnitude < distance * distance; }

        /// <summary>The sliding doors open for anyone walking within reach of them.</summary>
        private static void KeepDoorsOpen(Diner d)
        {
            Vector3 p = d.Motion.Body.position;
            if (RestaurantEnclosure.EntranceDoor != null && Near(p, new Vector3(RestaurantEnclosure.EntranceX, 0, RestaurantEnclosure.South), 1.3f)) RestaurantEnclosure.EntranceDoor.HoldOpen(.8f);
            if (RestaurantEnclosure.ExitDoor != null && Near(p, new Vector3(RestaurantEnclosure.ExitX, 0, RestaurantEnclosure.South), 1.3f)) RestaurantEnclosure.ExitDoor.HoldOpen(.8f);
        }

        // ---- moving ----

        /// <summary>Hold the person's standing footprint on the grid, as a parked robot does.</summary>
        private void Hold(Diner d, Vector3 at) { d.Motion.Wait(); traffic.Park(d.Id, footprint.At(Flat(at), d.Motion.Body.rotation.eulerAngles.y)); }

        private void Face(Diner d, Quaternion rotation) { d.Motion.Wait(); d.Motion.Body.MoveRotation(rotation); }

        /// <summary>
        /// Walk to a point through the dining room: with the shared planner (robots yield), or, when it cannot
        /// fit a route within 4 s (or the route stalls for 25 s), along the side aisles. True on arrival.
        /// </summary>
        private bool Go(Diner d, Vector3 goal, float clock, ref bool planned)
        {
            if (!d.Going) { d.Going = true; d.Goal = goal; d.GoSince = clock; d.Fallback = false; d.RetryAt = Mathf.Min(d.RetryAt, clock); }
            bool arrived;
            if (d.Fallback) arrived = FollowPath(d, Time.fixedDeltaTime, WalkSpeed);
            else
            {
                arrived = Travel(d, goal, clock, ref planned);
                if (!arrived && ((d.Plan == null && clock - d.GoSince > 4) || clock - d.GoSince > 25))
                {
                    d.Plan = null; traffic.Remove(d.Id);
                    SideAisles(d, goal); d.Fallback = true; d.Motion.Walk();
                    Debug.Log("[GuardianDiners] " + d.Id + " takes the side aisles (" + traffic.LastFailure + ")");
                }
            }
            if (arrived) { d.Going = false; d.Motion.Wait(); }
            return arrived;
        }

        /// <summary>A route along the south and east side aisles, clear of the tables, the line and the robots' lanes.</summary>
        private static void SideAisles(Diner d, Vector3 goal)
        {
            Vector3 from = d.Motion.Body.position;
            bool fromSouth = from.z < -4.2f, toSouth = goal.z < -4.2f;
            d.Path.Clear(); d.PathAt = 0;
            d.Path.Add(fromSouth ? new Vector3(from.x, 0, SouthAisleZ) : new Vector3(EastAisleX, 0, from.z));
            if (fromSouth != toSouth) d.Path.Add(new Vector3(EastAisleX, 0, SouthAisleZ));
            d.Path.Add(toSouth ? new Vector3(goal.x, 0, SouthAisleZ) : new Vector3(EastAisleX, 0, goal.z));
            d.Path.Add(new Vector3(goal.x, 0, goal.z));
        }

        /// <summary>Scripted walk through the points in d.Path. True at the last one.</summary>
        private bool FollowPath(Diner d, float dt, float speed)
        {
            while (d.PathAt < d.Path.Count && Glide(d, d.Path[d.PathAt], dt, speed)) d.PathAt++;
            return d.PathAt >= d.Path.Count;
        }

        /// <summary>Scripted walk in a straight line. True on arrival.</summary>
        private bool Glide(Diner d, Vector3 goal, float dt, float speed)
        {
            Vector3 p = d.Motion.Body.position, delta = goal - p;
            delta.y = 0;
            float distance = delta.magnitude;
            if (distance < .01f) return true;
            if (distance > .05f) d.Motion.Body.MoveRotation(Quaternion.RotateTowards(d.Motion.Body.rotation, Quaternion.LookRotation(delta), 360 * dt));
            d.Motion.Body.MovePosition(p + delta / distance * Mathf.Min(distance, speed * dt));
            return false;
        }

        /// <summary>Plan with the shared traffic, then follow the leg like the robots do. True on arrival.</summary>
        private bool Travel(Diner d, Vector3 goal, float clock, ref bool planned)
        {
            if (d.Plan == null)
            {
                Vector3 p = d.Motion.Body.position;
                if (FloorPoint.Distance(Flat(p), Flat(goal)) < .1f) return true;
                if (planned || clock < d.RetryAt || !traffic.TakePedestrianTurn(clock)) return false;
                planned = true; d.RetryAt = clock + 1;
                if (traffic.Request(d.Id, Flat(p), d.Motion.Body.rotation.eulerAngles.y, footprint, Flat(goal), false, false, 0, out var plan) == GuardianDirectRequestResult.Committed)
                { d.Plan = plan; d.Node = 1; d.Progress = 0; d.Motion.Walk(); }
                return false;
            }
            Vector3 at = d.Motion.Body.position;
            d.Progress = Mathf.Max(d.Progress, GuardianDirectControl.Project(d.Plan, d.Node, Flat(at)));
            traffic.UpdateProgress(d.Id, d.Progress);
            var control = GuardianDirectControl.Step(d.Plan, new GuardianDirectControl.Input
            {
                Position = Flat(at), Velocity = Flat(d.Motion.Velocity), Yaw = d.Motion.Body.rotation.eulerAngles.y, YawRate = 0,
                MaximumSpeed = d.Guest.RigSettings.walkSpeed, Acceleration = 2, Frontier = traffic.Frontier(d.Id), Progress = d.Progress, Node = d.Node
            });
            if (control.AdvanceNode) d.Node = Mathf.Min(d.Node + 1, d.Plan.Points.Count - 1);
            if (control.Arrived) { traffic.Complete(d.Id); d.Plan = null; d.Motion.Wait(); return true; }
            d.Motion.MoveAlongRoute(new Vector3(control.Velocity.X, 0, control.Velocity.Z), control.Yaw, Time.fixedDeltaTime);
            return false;
        }

        // ---- signs ----

        private void EnsureFont()
        {
            if (font == null) font = GuardianFonts.Create(48);
        }

        /// <summary>A small text that always faces the camera (seat labels, the staff tag).</summary>
        private TextMesh Tag(Transform parent, string text, Color color)
        {
            EnsureFont();
            var go = new GameObject("Label " + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.up * 2.05f;
            var t = go.AddComponent<TextMesh>();
            t.font = font; t.fontSize = 48; t.characterSize = .035f;
            t.anchor = TextAnchor.MiddleCenter; t.alignment = TextAlignment.Center; t.text = text; t.color = color;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            return t;
        }

        private void EnsureSign(Transform environment)
        {
            if (sign != null) return;
            Transform kiosk = environment.Find(RestaurantEnclosure.RootName + "/" + RestaurantEnclosure.KioskName);
            if (kiosk == null) return;
            sign = Tag(kiosk, "セルフレジ", Color.white);
            sign.characterSize = .045f;
            sign.transform.localPosition = new Vector3(0, 1.85f, 0);
        }

        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            Quaternion facing = cam.transform.rotation;
            if (sign != null && sign.gameObject.activeInHierarchy) sign.transform.rotation = facing;
            if (staffTag != null) staffTag.transform.rotation = facing;
            foreach (var s in seats) if (s.Label != null && s.Label.gameObject.activeSelf) s.Label.transform.rotation = facing;
        }

        private void OnDestroy()
        {
            if (sim != null) sim.PhysicsStepBeginning -= Step;
            foreach (var d in diners) traffic?.Remove(d.Id);
            if (staff != null) traffic?.Remove(staff.Id);
            GuardianFonts.Release(font);
        }
    }
}
