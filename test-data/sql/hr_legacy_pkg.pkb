CREATE OR REPLACE PACKAGE BODY hr_legacy_pkg AS

    PROCEDURE process_hire (
        p_first_name   IN  VARCHAR2,
        p_last_name    IN  VARCHAR2,
        p_dept_id      IN  NUMBER,
        pSalary        IN  NUMBER,
        pHireDate      IN  DATE,
        p_email        IN  VARCHAR2,
        p_status_code  IN  NUMBER,
        p_audit_user   IN  VARCHAR2,
        p_emp_id       OUT NUMBER
    ) IS
        v_emp_id  NUMBER;
        v_dummy   NUMBER;
    BEGIN
        v_dummy := 0;
        v_dummy := 999;

        SELECT NVL(MAX(emp_id), 0) + 1
          INTO v_emp_id
          FROM employees;

        IF p_status_code = 1 THEN
            INSERT INTO employees (emp_id, dept_id, first_name, last_name, email, hire_date, salary, status)
            VALUES (v_emp_id, p_dept_id, p_first_name, p_last_name, p_email, pHireDate, pSalary, 'ACTIVE');
        ELSIF p_status_code = 2 THEN
            INSERT INTO employees (emp_id, dept_id, first_name, last_name, email, hire_date, salary, status)
            VALUES (v_emp_id, p_dept_id, p_first_name, p_last_name, p_email, pHireDate, pSalary, 'LEAVE');
        ELSIF p_status_code = 3 THEN
            INSERT INTO employees (emp_id, dept_id, first_name, last_name, email, hire_date, salary, status)
            VALUES (v_emp_id, p_dept_id, p_first_name, p_last_name, p_email, pHireDate, pSalary, 'TERMINATED');
        END IF;

        INSERT INTO audit_log (audit_id, action_type, object_name, details)
        VALUES (audit_seq.NEXTVAL, 'INSERT', 'EMPLOYEES', 'New hire: ' || p_first_name || ' ' || p_last_name);

        p_emp_id := v_emp_id;
    EXCEPTION
        WHEN DUP_VAL_ON_INDEX THEN
            INSERT INTO audit_log (audit_id, action_type, object_name, details)
            VALUES (audit_seq.NEXTVAL, 'INSERT', 'EMPLOYEES', 'Duplicate hire skipped for ' || p_email);
            RAISE;
        WHEN OTHERS THEN
            INSERT INTO audit_log (audit_id, action_type, object_name, details)
            VALUES (audit_seq.NEXTVAL, 'INSERT', 'EMPLOYEES', 'Hire failed for ' || p_email || ': ' || SQLERRM);
            RAISE;
    END process_hire;


    PROCEDURE archive_terminated_employees IS
    BEGIN
        FOR rec IN (SELECT emp_id FROM employees WHERE status = 'TERMINATED') LOOP
            INSERT INTO audit_log (audit_id, action_type, object_name, details)
            VALUES (audit_seq.NEXTVAL, 'DELETE', 'EMPLOYEES', 'Archive: ' || rec.emp_id);
            DELETE FROM employees WHERE emp_id = rec.emp_id;
        END LOOP;
        COMMIT;
    END archive_terminated_employees;

END hr_legacy_pkg;
/
