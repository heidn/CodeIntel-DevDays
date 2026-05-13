CREATE OR REPLACE PACKAGE BODY payroll_pkg AS

    PROCEDURE apply_dept_raise (
        p_dept_id  IN  NUMBER,
        p_pct      IN  NUMBER
    ) IS
        CURSOR c_emps IS
            SELECT emp_id, salary
              FROM employees
             WHERE dept_id = p_dept_id
               AND status  = 'ACTIVE';
        v_emp_id   employees.emp_id%TYPE;
        v_salary   employees.salary%TYPE;
        v_new_sal  NUMBER;
    BEGIN
        OPEN c_emps;
        LOOP
            FETCH c_emps INTO v_emp_id, v_salary;
            EXIT WHEN c_emps%NOTFOUND;
            v_new_sal := v_salary * (1 + p_pct / 100);
            UPDATE employees
               SET salary = v_new_sal
             WHERE emp_id = v_emp_id;
        END LOOP;
        COMMIT;
    EXCEPTION
        WHEN OTHERS THEN
            NULL;
    END apply_dept_raise;


    PROCEDURE deactivate_terminated IS
    BEGIN
        UPDATE employees
           SET status = 'TERMINATED';
        COMMIT;
    END deactivate_terminated;


    PROCEDURE lookup_employee (
        p_emp_id  IN  NUMBER,
        p_name    OUT VARCHAR2
    ) IS
    BEGIN
        SELECT first_name || ' ' || last_name
          INTO p_name
          FROM employees
         WHERE emp_id = p_emp_id;
    END lookup_employee;


    PROCEDURE count_blanks (p_count OUT NUMBER) IS
    BEGIN
        SELECT COUNT(*)
          INTO p_count
          FROM employees
         WHERE termination_date = NULL;
    END count_blanks;


    PROCEDURE run_dynamic_update (p_status IN VARCHAR2) IS
        v_sql VARCHAR2(500);
    BEGIN
        v_sql := 'UPDATE employees SET status = ''' || p_status || '''';
        EXECUTE IMMEDIATE v_sql;
    END run_dynamic_update;

END payroll_pkg;
/
